namespace Odoo.WebUI.Services
{
    // ── Etapa 2: POS configuration ──────────────────────────────
    public sealed record PosConfigDetail(
        int Id,
        string Name,
        int CurrencyId,
        string CurrencyName,
        int PricelistId,
        List<int> PaymentMethodIds);

    // ── Etapa 6: Session Load Data (bundle para pintar el POS) ──
    public sealed record PosPaymentMethodDto(int Id, string Name, bool IsCash);

    public sealed record PosTaxDto(int Id, string Name, decimal Amount);

    public sealed record PosSessionData(
        PosSessionInfo Session,
        string ConfigName,
        List<PosPaymentMethodDto> PaymentMethods,
        List<PosTaxDto> Taxes);

    // ── Etapa 7/8: productos ────────────────────────────────────
    public sealed record PosProduct(
        int Id,
        string Name,
        decimal Price,
        decimal QtyAvailable,
        string Barcode,
        string Category,
        List<int> TaxIds,
        List<int>? PosCategIds = null);

    // ── Categorías POS (pos.category) ───────────────────────────
    public sealed record PosCategoryDto(int Id, string Name, int ParentId);

    public sealed record PosProductInfo(
        int Id,
        string Name,
        decimal Price,
        decimal QtyAvailable,
        string Barcode,
        string Category,
        string Uom,
        string Description,
        List<int> TaxIds);

    // ── Clientes (res.partner) ────────────────────────────────
    public sealed record PosCustomerDto(int Id, string Name, string Email, string Phone, string Vat)
    {
        public string Label => string.IsNullOrEmpty(Vat) ? Name : $"{Name} (RFC: {Vat})";
    }

    // ── Etapa 11/12: cierre ─────────────────────────────────────
    public sealed record PosClosingPayment(int MethodId, string MethodName, decimal Amount, bool IsCash);

    public sealed record PosClosingControlData(
        int SessionId,
        int OrdersCount,
        decimal TotalAmount,
        decimal ExpectedCash,
        List<PosClosingPayment> Payments,
        bool HasOfficialData,
        string LoadWarning = "");

    public sealed record PosClosingDetails(decimal CashCounted, string? Notes)
    {
        public Dictionary<string, object> ToDict() => new()
        {
            ["cash_counted"] = CashCounted,
            ["notes"] = Notes ?? string.Empty,
        };
    }
}
