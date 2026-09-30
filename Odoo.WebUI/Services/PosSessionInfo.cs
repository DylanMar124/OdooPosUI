namespace Odoo.WebUI.Services
{
    /// <summary>
    /// Sesión de caja POS con su estado de control de efectivo.
    /// Odoo: 'opening_control' = creada pero sin iniciar (falta efectivo inicial),
    /// 'opened' = operando, 'closing_control'/'closed' = en cierre/cerrada.
    /// </summary>
    public sealed class PosSessionInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int ConfigId { get; set; }

        public bool IsOpened => State == "opened";
        public bool NeedsStart => State == "opening_control";
        public bool IsClosing => State == "closing_control";
    }
}
