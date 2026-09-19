// Pago con tarjeta en la misma web (Mercado Pago · Checkout Bricks).
// La tarjeta se tokeniza en el navegador con la Public Key; los datos de la tarjeta
// NUNCA pasan por nuestro servidor. onSubmit envía el token a .NET, que crea el pago.
window.simberMp = {
    _mp: null,
    _controller: null,

    async render(publicKey, amount, containerId, dotNetRef) {
        if (!window.MercadoPago) {
            throw new Error("No se pudo cargar Mercado Pago. Revisa tu conexión.");
        }

        await this.destroy();

        this._mp = new MercadoPago(publicKey, { locale: "es-PE" });
        const bricks = this._mp.bricks();

        const settings = {
            initialization: { amount: amount },
            customization: {
                visual: { style: { theme: "dark" } },
                paymentMethods: { maxInstallments: 1 }
            },
            callbacks: {
                onReady: () => { },
                onError: (error) => {
                    dotNetRef.invokeMethodAsync(
                        "OnBrickError",
                        (error && error.message) ? error.message : "Revisa los datos de la tarjeta.");
                },
                onSubmit: (formData) => {
                    return dotNetRef
                        .invokeMethodAsync("OnBrickSubmit", JSON.stringify(formData))
                        .then((resultJson) => {
                            let res = {};
                            try { res = JSON.parse(resultJson); } catch (e) { }
                            if (res.status === "approved" || res.status === "pending") {
                                return Promise.resolve();
                            }
                            return Promise.reject();
                        })
                        .catch(() => Promise.reject());
                }
            }
        };

        this._controller = await bricks.create("cardPayment", containerId, settings);
    },

    async destroy() {
        try {
            if (this._controller) {
                await this._controller.unmount();
            }
        } catch (e) { }
        this._controller = null;
    }
};
