// media-guard.js — frena la copia casual de los diseños.
// NOTA: esto solo detiene al usuario comun (clic derecho / arrastrar / "abrir imagen
// en pestana nueva"). Quien use DevTools o la pestana de red siempre podra ver la URL
// publica de la imagen. La defensa real es la marca de agua grabada en la foto.
(function () {
    "use strict";

    function isProtected(el) {
        if (!el) return false;
        if (el.tagName === "IMG") {
            // Permitir iconos/marca con data-allow-copy si algun dia hace falta.
            return el.dataset.allowCopy !== "true";
        }
        return typeof el.closest === "function" && el.closest(".media-guard") !== null;
    }

    // 1) Clic derecho sobre una imagen protegida: sin menu => sin "abrir imagen / guardar".
    document.addEventListener("contextmenu", function (e) {
        if (isProtected(e.target)) e.preventDefault();
    }, { capture: true });

    // 2) Arrastrar la imagen al escritorio o a otra pestana.
    document.addEventListener("dragstart", function (e) {
        if (isProtected(e.target)) e.preventDefault();
    }, { capture: true });

    // La pulsacion larga en iOS la frena el CSS (-webkit-touch-callout: none),
    // declarado de entrada, que es mas robusto que hacerlo en touchstart.
})();
