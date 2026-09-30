/* Impresión térmica aislada: el ticket se escribe como documento standalone
   en un iframe oculto y se imprime ese documento. Así ningún script ni estilo
   de la página (Tailwind CDN, Blazor, Bootstrap) interfiere con la impresión. */
window.odooPrint = {
    printHtml: function (frameId, html) {
        try {
            var frame = document.getElementById(frameId);
            if (!frame || !frame.contentWindow) {
                console.error('odooPrint: iframe no encontrado: ' + frameId);
                return false;
            }
            var win = frame.contentWindow;
            var doc = win.document;
            doc.open();
            doc.write(html);
            doc.close();
            win.focus();
            win.print();
            return true;
        } catch (e) {
            console.error('odooPrint: fallo al imprimir', e);
            return false;
        }
    }
};
