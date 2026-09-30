namespace Odoo.WebUI.Services
{
    /// <summary>
    /// Línea del ticket compartida entre Pos.razor y OdooService.
    /// Vive en Services para que CrearOrdenPosAsync(List&lt;CartItem&gt;) compile.
    /// </summary>
    public sealed class CartItem
    {
        public int ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int Qty { get; set; }
        public decimal LineTotal => UnitPrice * Qty;
        /// <summary>Existencia al agregar (tope por línea; se refresca en cada alta).</summary>
        public decimal StockQty { get; set; }
        /// <summary>Ids de account.tax del producto (vacío = exento).</summary>
        public List<int> TaxIds { get; set; } = new();
        /// <summary>Suma de tasas (0.16m = 16%). Se asumen impuestos EXCLUIDOS del precio.</summary>
        public decimal TaxRate { get; set; }
        public decimal LineTax => Math.Round(LineTotal * TaxRate, 2);
    }
}
