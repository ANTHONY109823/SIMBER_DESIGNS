-- ============================================================================
-- Simber Designs — esquema canónico (créditos + membresías + pgvector)
-- Los ZIP/RAR de ~100 MB viven en Cloudflare R2. Aquí solo claves y metadatos.
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Limpieza del MVP Free/Pro y recréación del modelo HF
DROP TABLE IF EXISTS download_logs CASCADE;
DROP TABLE IF EXISTS payments CASCADE;
DROP TABLE IF EXISTS orders CASCADE;
DROP TABLE IF EXISTS user_downloads CASCADE;
DROP TABLE IF EXISTS credit_transactions CASCADE;
DROP TABLE IF EXISTS transactions CASCADE;
DROP TABLE IF EXISTS designs CASCADE;
DROP TABLE IF EXISTS subscriptions CASCADE;
DROP TABLE IF EXISTS credit_packages CASCADE;
DROP TABLE IF EXISTS users CASCADE;

DROP TYPE IF EXISTS credit_tx_type CASCADE;
DROP TYPE IF EXISTS subscription_status CASCADE;
DROP TYPE IF EXISTS subscription_tier CASCADE;
DROP TYPE IF EXISTS transaction_status CASCADE;
DROP TYPE IF EXISTS gateway_type CASCADE;
DROP TYPE IF EXISTS user_role CASCADE;

CREATE TYPE user_role AS ENUM ('Admin', 'Customer', 'Designer');
CREATE TYPE gateway_type AS ENUM ('LemonSqueezy', 'PayPal', 'Manual_QR', 'Transfer');
CREATE TYPE transaction_status AS ENUM ('Pending', 'Completed', 'Failed', 'Refunded', 'Rejected');
CREATE TYPE subscription_tier AS ENUM ('Basic', 'VIP', 'Semestral');
CREATE TYPE subscription_status AS ENUM ('Active', 'Canceled', 'Expired');
CREATE TYPE credit_tx_type AS ENUM ('Recharge', 'Purchase_Design', 'Refund');

CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    email VARCHAR(255) UNIQUE NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    full_name VARCHAR(150),
    role user_role DEFAULT 'Customer'::user_role NOT NULL,
    credits_balance DECIMAL(10, 2) DEFAULT 0.00 NOT NULL CHECK (credits_balance >= 0.00),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

CREATE TABLE subscriptions (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    tier subscription_tier NOT NULL,
    status subscription_status DEFAULT 'Active'::subscription_status NOT NULL,
    daily_download_limit INT NOT NULL CHECK (daily_download_limit >= 0),
    starts_at TIMESTAMP WITH TIME ZONE NOT NULL,
    expires_at TIMESTAMP WITH TIME ZONE NOT NULL,
    external_sub_id VARCHAR(100),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    CONSTRAINT chk_dates CHECK (expires_at > starts_at)
);

CREATE TABLE credit_packages (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name VARCHAR(50) NOT NULL,
    credits_amount DECIMAL(10, 2) NOT NULL CHECK (credits_amount > 0),
    bonus_amount DECIMAL(10, 2) DEFAULT 0.00 NOT NULL CHECK (bonus_amount >= 0.00),
    price_usd DECIMAL(10, 2) NOT NULL CHECK (price_usd > 0),
    active BOOLEAN DEFAULT TRUE NOT NULL
);

CREATE TABLE designs (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    title VARCHAR(255) NOT NULL,
    slug VARCHAR(255) UNIQUE NOT NULL,
    description TEXT,
    category VARCHAR(100) NOT NULL DEFAULT 'Jersey',
    price_usd DECIMAL(10, 2) DEFAULT 2.00 NOT NULL CHECK (price_usd >= 0.00),
    credits_cost DECIMAL(10, 2) DEFAULT 2.00 NOT NULL CHECK (credits_cost >= 0.00),
    r2_key VARCHAR(512) NOT NULL,
    preview_url VARCHAR(512) NOT NULL,
    is_free_daily BOOLEAN DEFAULT TRUE NOT NULL,
    embedding vector(512),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

CREATE TABLE transactions (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    amount DECIMAL(10, 2) NOT NULL CHECK (amount >= 0.00),
    currency VARCHAR(3) DEFAULT 'USD' NOT NULL,
    gateway gateway_type NOT NULL,
    status transaction_status DEFAULT 'Pending'::transaction_status NOT NULL,
    external_reference_id VARCHAR(255),
    payment_receipt_url VARCHAR(512),
    credit_package_id UUID REFERENCES credit_packages(id) ON DELETE SET NULL,
    subscription_id UUID REFERENCES subscriptions(id) ON DELETE SET NULL,
    design_id UUID REFERENCES designs(id) ON DELETE SET NULL,
    notes TEXT,
    verified_by UUID REFERENCES users(id),
    verified_at TIMESTAMP WITH TIME ZONE,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

CREATE TABLE credit_transactions (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    transaction_id UUID REFERENCES transactions(id) ON DELETE SET NULL,
    design_id UUID REFERENCES designs(id) ON DELETE SET NULL,
    credits_changed DECIMAL(10, 2) NOT NULL,
    tx_type credit_tx_type NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

CREATE TABLE user_downloads (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    design_id UUID NOT NULL REFERENCES designs(id) ON DELETE RESTRICT,
    downloaded_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    ip_address VARCHAR(45) NOT NULL,
    user_agent TEXT NOT NULL
);

CREATE INDEX idx_users_email ON users(email);
CREATE INDEX idx_user_downloads_limit ON user_downloads(user_id, downloaded_at);
CREATE INDEX idx_transactions_status_pending ON transactions(status) WHERE status = 'Pending';
CREATE INDEX idx_designs_slug ON designs(slug);
CREATE INDEX idx_designs_category ON designs(category);
CREATE INDEX idx_designs_embedding_cosine ON designs USING hnsw (embedding vector_cosine_ops);

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER tr_users_updated_at BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER tr_designs_updated_at BEFORE UPDATE ON designs
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER tr_transactions_updated_at BEFORE UPDATE ON transactions
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER tr_subscriptions_updated_at BEFORE UPDATE ON subscriptions
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- Acredita créditos en INSERT Completed (Lemon Squeezy) y en UPDATE Pending→Completed (Yape).
CREATE OR REPLACE FUNCTION process_approved_credit_recharge()
RETURNS TRIGGER AS $$
DECLARE
    credits_to_add DECIMAL(10, 2);
    already_processed BOOLEAN;
BEGIN
    IF NEW.status IS DISTINCT FROM 'Completed' OR NEW.credit_package_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE' AND OLD.status = 'Completed' THEN
        RETURN NEW;
    END IF;

    SELECT EXISTS(
        SELECT 1 FROM credit_transactions
        WHERE transaction_id = NEW.id AND tx_type = 'Recharge'
    ) INTO already_processed;

    IF already_processed THEN
        RETURN NEW;
    END IF;

    SELECT (credits_amount + bonus_amount) INTO credits_to_add
    FROM credit_packages
    WHERE id = NEW.credit_package_id;

    IF credits_to_add IS NULL THEN
        RETURN NEW;
    END IF;

    UPDATE users
    SET credits_balance = credits_balance + credits_to_add
    WHERE id = NEW.user_id;

    INSERT INTO credit_transactions (user_id, transaction_id, credits_changed, tx_type)
    VALUES (NEW.user_id, NEW.id, credits_to_add, 'Recharge');

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER tr_transactions_credits_update
    AFTER UPDATE ON transactions
    FOR EACH ROW EXECUTE FUNCTION process_approved_credit_recharge();

CREATE TRIGGER tr_transactions_credits_insert
    AFTER INSERT ON transactions
    FOR EACH ROW EXECUTE FUNCTION process_approved_credit_recharge();

INSERT INTO credit_packages (name, credits_amount, bonus_amount, price_usd) VALUES
('Basic', 10.00, 0.00, 10.00),
('VIP', 25.00, 5.00, 25.00),
('Elite', 50.00, 15.00, 50.00);
