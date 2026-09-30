using System.Text.Json;

namespace Odoo.WebUI.Services
{
    /// <summary>
    /// Pipeline oficial del POS de Odoo, 1:1 con el frontend (pos_app):
    /// Auth → Config → OpenRegister → Session → OpeningControl → LoadData →
    /// Products → ProductInfo → Order(sync_from_ui) → Alternate(create) →
    /// ClosingData → PostClosing.
    ///
    /// Auth ("bearer"): Odoo no emite JWT para POS; el equivalente es uid + password
    /// (acepta API key) por llamada JSON-RPC, persistido por OdooAuthState.
    /// Esta clase asume sesión válida (OdooService.SetSession vía AuthState).
    /// Todo con new object[] en args (evita CS0826). .NET 10, sin MudBlazor.
    /// </summary>
    public class PosPipelineService(OdooService odoo)
    {
        // ── Etapa 2: POS configuration ──────────────────────────
        public async Task<List<PosConfigDetail>> GetConfigurationsAsync()
        {
            var rows = await odoo.SearchReadAsync(
                "pos.config",
                new object[0],
                new string[] { "id", "name", "currency_id", "pricelist_id", "payment_method_ids" },
                20);

            return rows.Select(r => new PosConfigDetail(
                Id: GetInt(r, "id"),
                Name: GetString(r, "name"),
                CurrencyId: FirstId(GetField(r, "currency_id")),
                CurrencyName: GetSecondName(GetField(r, "currency_id")),
                PricelistId: FirstId(GetField(r, "pricelist_id")),
                PaymentMethodIds: GetIdList(GetField(r, "payment_method_ids"))
            )).Where(c => c.Id > 0).ToList();
        }

        // ── Etapa 3: POS open register (SOLO crea la sesión, como el dashboard) ──
        public Task<int> OpenRegisterAsync(int configId)
            => odoo.CreateAsync(
                "pos.session",
                new Dictionary<string, object> { ["config_id"] = configId });

        // ── Etapa 4: Get POS Session ────────────────────────────
        public async Task<PosSessionInfo?> GetSessionAsync(int sessionId)
        {
            var rows = await odoo.SearchReadAsync(
                "pos.session",
                new object[] { new object[] { "id", "=", sessionId } },
                new string[] { "id", "name", "config_id", "user_id", "state", "start_at", "stop_at" },
                1);
            if (rows.Count == 0)
                return null;

            var r = rows[0];
            return new PosSessionInfo
            {
                Id = GetInt(r, "id"),
                Name = GetString(r, "name"),
                State = GetString(r, "state"),
                ConfigId = FirstId(GetField(r, "config_id"))
            };
        }

        // ── Etapa 5: Set Opening Control (efectivo inicial + apertura) ──
        public Task<int> SetOpeningControlAsync(int sessionId, decimal cash, string? notes = null)
            => odoo.StartPosSessionAsync(sessionId, cash, notes);

        // ── Etapa 6: POS Session Load Data (bundle único para la UI) ──
        public async Task<PosSessionData> LoadSessionDataAsync(int sessionId)
        {
            var session = await GetSessionAsync(sessionId)
                ?? throw new InvalidOperationException($"Sesión #{sessionId} no encontrada.");

            var cfgRows = await odoo.SearchReadAsync(
                "pos.config",
                new object[] { new object[] { "id", "=", session.ConfigId } },
                new string[] { "id", "name", "payment_method_ids" },
                1);

            var configName = cfgRows.Count > 0 ? GetString(cfgRows[0], "name") : string.Empty;
            var pmIds = cfgRows.Count > 0 ? GetIdList(GetField(cfgRows[0], "payment_method_ids")) : new();

            var methods = new List<PosPaymentMethodDto>();
            if (pmIds.Count > 0)
            {
                var pmRows = await odoo.SearchReadAsync(
                    "pos.payment.method",
                    new object[] { new object[] { "id", "in", pmIds.Cast<object>().ToArray() } },
                    new string[] { "id", "name", "type" },
                    20);
                methods = pmRows.Select(m => new PosPaymentMethodDto(
                    GetInt(m, "id"),
                    GetString(m, "name"),
                    GetString(m, "type") == "cash")).Where(m => m.Id > 0).ToList();
            }

            var taxRows = await odoo.SearchReadAsync(
                "account.tax",
                new object[] { new object[] { "type_tax_use", "=", "sale" }, new object[] { "active", "=", true } },
                new string[] { "id", "name", "amount" },
                50);
            var taxes = taxRows.Select(t => new PosTaxDto(
                GetInt(t, "id"), GetString(t, "name"), GetDecimal(t, "amount"))).ToList();

            return new PosSessionData(session, configName, methods, taxes);
        }

        // ── Etapa 7: POS products jsonAPI ───────────────────────
        public async Task<List<PosProduct>> GetProductsAsync(int limit = 200, string? search = null)
        {
            var domain = new List<object> { new object[] { "available_in_pos", "=", true } };
            if (!string.IsNullOrWhiteSpace(search))
                domain.Add(new object[] { "display_name", "ilike", search.Trim() });

            var rows = await odoo.SearchReadAsync(
                "product.product",
                domain.ToArray(),
                new string[] { "id", "display_name", "list_price", "qty_available", "barcode", "categ_id", "taxes_id" },
                limit);

            return rows.Select(r => new PosProduct(
                GetInt(r, "id"),
                GetString(r, "display_name"),
                GetDecimal(r, "list_price"),
                GetDecimal(r, "qty_available"),
                GetString(r, "barcode"),
                GetSecondName(GetField(r, "categ_id")),
                GetIdList(GetField(r, "taxes_id"))
            )).ToList();
        }

        // ── Categorías POS con restricción del config ────────────
        // Oficial: limit_categories + iface_available_categ_ids (+ descendientes).
        // Sin restricción (o sin config) devuelve todas, ordenadas por secuencia.
        public async Task<List<PosCategoryDto>> GetPosCategoriesAsync(int? configId)
        {
            var (limited, allowed) = await ReadCategoryRestrictionAsync(configId);
            var rows = await odoo.SearchReadAsync(
                "pos.category",
                new object[0],
                new string[] { "id", "name", "parent_id" },
                200,
                "sequence");
            var all = rows.Select(r => new PosCategoryDto(
                GetInt(r, "id"),
                GetString(r, "name"),
                FirstId(GetField(r, "parent_id")))).Where(c => c.Id > 0).ToList();

            if (!limited || allowed.Count == 0)
                return all;

            var scope = ExpandDescendants(all, allowed);
            return all.Where(c => scope.Contains(c.Id)).ToList();
        }

        // ── Etapa 7b: productos del POS (surtido oficial por caja) ──
        // Dominio oficial: available_in_pos + sale_ok + pos_categ_ids (si restringe).
        public async Task<List<PosProduct>> GetPosProductsAsync(int? configId, int limit = 200, string? search = null)
        {
            var domain = new List<object>
            {
                new object[] { "available_in_pos", "=", true },
                new object[] { "sale_ok", "=", true }
            };

            var (limited, allowed) = await ReadCategoryRestrictionAsync(configId);
            if (limited && allowed.Count > 0)
            {
                var tree = await odoo.SearchReadAsync(
                    "pos.category",
                    new object[0],
                    new string[] { "id", "parent_id" },
                    500);
                var scope = ExpandDescendants(
                    tree.Select(r => new PosCategoryDto(GetInt(r, "id"), string.Empty, FirstId(GetField(r, "parent_id")))).ToList(),
                    allowed);
                domain.Add(new object[] { "pos_categ_ids", "in", scope.Cast<object>().ToArray() });
            }

            if (!string.IsNullOrWhiteSpace(search))
                domain.Add(new object[] { "display_name", "ilike", search.Trim() });

            var rows = await odoo.SearchReadAsync(
                "product.product",
                domain.ToArray(),
                new string[] { "id", "display_name", "list_price", "qty_available", "barcode", "categ_id", "pos_categ_ids", "taxes_id" },
                limit);

            return rows.Select(r => new PosProduct(
                GetInt(r, "id"),
                GetString(r, "display_name"),
                GetDecimal(r, "list_price"),
                GetDecimal(r, "qty_available"),
                GetString(r, "barcode"),
                GetSecondName(GetField(r, "categ_id")),
                GetIdList(GetField(r, "taxes_id")),
                GetIdList(GetField(r, "pos_categ_ids"))
            )).ToList();
        }

        private async Task<(bool Limited, List<int> Allowed)> ReadCategoryRestrictionAsync(int? configId)
        {
            if (!configId.HasValue || configId.Value <= 0)
                return (false, new());
            var rows = await odoo.SearchReadAsync(
                "pos.config",
                new object[] { new object[] { "id", "=", configId.Value } },
                new string[] { "id", "limit_categories", "iface_available_categ_ids" },
                1);
            if (rows.Count == 0)
                return (false, new());
            return (GetBool(rows[0], "limit_categories"), GetIdList(GetField(rows[0], "iface_available_categ_ids")));
        }

        private static HashSet<int> ExpandDescendants(List<PosCategoryDto> all, List<int> roots)
        {
            var kids = all.GroupBy(c => c.ParentId).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
            var scope = new HashSet<int>(roots);
            var stack = new Stack<int>(roots);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!kids.TryGetValue(cur, out var list)) continue;
                foreach (var k in list)
                    if (scope.Add(k))
                        stack.Push(k);
            }
            return scope;
        }

        /// <summary>
        /// Existencias actuales por lote (multicancha): una sola lectura para todo el ticket.
        /// Devuelve productId → qty_available. Si falla, quien llama decide (fail-open).
        /// </summary>
        public async Task<Dictionary<int, decimal>> GetStockAsync(List<int> productIds)
        {
            var ids = productIds.Where(id => id > 0).Distinct().Cast<object>().ToArray();
            var result = new Dictionary<int, decimal>();
            if (ids.Length == 0)
                return result;

            var rows = await odoo.SearchReadAsync(
                "product.product",
                new object[] { new object[] { "id", "in", ids } },
                new string[] { "id", "qty_available" },
                ids.Length);
            foreach (var r in rows)
            {
                var id = GetInt(r, "id");
                if (id > 0)
                    result[id] = GetDecimal(r, "qty_available");
            }
            return result;
        }

        // ── Etapa 8: Product info API ───────────────────────────
        public async Task<PosProductInfo?> GetProductInfoAsync(int productId)
        {
            var rows = await odoo.SearchReadAsync(
                "product.product",
                new object[] { new object[] { "id", "=", productId } },
                new string[] { "id", "display_name", "list_price", "qty_available", "barcode", "categ_id", "uom_id", "description_sale", "taxes_id" },
                1);
            if (rows.Count == 0)
                return null;

            var r = rows[0];
            return new PosProductInfo(
                GetInt(r, "id"),
                GetString(r, "display_name"),
                GetDecimal(r, "list_price"),
                GetDecimal(r, "qty_available"),
                GetString(r, "barcode"),
                GetSecondName(GetField(r, "categ_id")),
                GetSecondName(GetField(r, "uom_id")),
                GetString(r, "description_sale"),
                GetIdList(GetField(r, "taxes_id")));
        }

        // ── Etapa 9: POS Order Case (pipeline oficial sync_from_ui) ──
        public Task<int> CreateOrderAsync(int sessionId, List<CartItem> cart, decimal total, int? paymentMethodId = null, bool toInvoice = false, int? partnerId = null)
            => odoo.CrearDesdeUiAsync(sessionId, cart, total, paymentMethodId, toInvoice, partnerId);

        // ── Clientes del POS (res.partner) ───────────────────────
        // Solo clientes (customer_rank > 0) para no mezclar proveedores.
        public async Task<List<PosCustomerDto>> GetCustomersAsync(int limit = 100, string? search = null)
        {
            var domain = new List<object> { new object[] { "customer_rank", ">", 0 } };
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                domain.Add(new object[] { "|", "|", new object[] { "name", "ilike", s }, new object[] { "email", "ilike", s }, new object[] { "phone", "ilike", s } });
            }

            var rows = await odoo.SearchReadAsync(
                "res.partner",
                domain.ToArray(),
                new string[] { "id", "name", "email", "phone", "vat" },
                limit,
                "name");
            return rows.Select(r => new PosCustomerDto(
                GetInt(r, "id"),
                GetString(r, "name"),
                GetString(r, "email"),
                GetString(r, "phone"),
                GetString(r, "vat"))).Where(c => c.Id > 0).ToList();
        }

        public async Task<int> CreateCustomerAsync(string name, string? email = null, string? phone = null, string? vat = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("El nombre es obligatorio.", nameof(name));

            var vals = new Dictionary<string, object> { ["name"] = name.Trim() };
            if (!string.IsNullOrWhiteSpace(email)) vals["email"] = email.Trim();
            if (!string.IsNullOrWhiteSpace(phone)) vals["phone"] = phone.Trim();
            if (!string.IsNullOrWhiteSpace(vat)) vals["vat"] = vat.Trim();
            return await odoo.CreateAsync("res.partner", vals);
        }

        // ── Etapa 10: Alternate JsonAPI pos order create (create clásico) ──
        // Útil si tu versión no soporta sync_from_ui o para órdenes borrador.
        // Manda los 5 NOT NULL + líneas [0,0,vals] con tax_ids y full_product_name.
        public async Task<int> CreateOrderDirectAsync(int sessionId, decimal subtotal, decimal iva, decimal total, List<CartItem> cart)
        {
            if (sessionId <= 0)
                throw new ArgumentException("Sesión POS inválida.", nameof(sessionId));
            if (cart is null || cart.Count == 0)
                throw new ArgumentException("El carrito está vacío.", nameof(cart));

            var lines = new object[cart.Count];
            for (var i = 0; i < cart.Count; i++)
            {
                var l = cart[i];
                lines[i] = new object[] { 0, 0, new Dictionary<string, object>
                {
                    ["product_id"] = l.ProductId,
                    ["qty"] = l.Qty,
                    ["price_unit"] = l.UnitPrice,
                    ["discount"] = 0,
                    ["tax_ids"] = new object[] { new object[] { 6, 0, new object[0] } },
                    ["full_product_name"] = l.Name ?? string.Empty
                } };
            }

            return await odoo.CreateAsync("pos.order", new Dictionary<string, object>
            {
                ["session_id"] = sessionId,
                ["pos_reference"] = $"WebUI-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}",
                ["amount_tax"] = iva,
                ["amount_total"] = total,
                ["amount_paid"] = 0m,
                ["amount_return"] = 0m,
                ["lines"] = lines
            });
        }

        // ── Etapa 11: Get closing control data ──────────────────
        public async Task<PosClosingControlData> GetClosingControlDataAsync(int sessionId)
        {
            var hasOfficial = false;
            try
            {
                await odoo.ExecuteKwAsync("pos.session", "get_closing_control_data", new object[] { new object[] { sessionId } });
                hasOfficial = true;
            }
            catch
            {
                // Versiones sin el método: se calculan totales manuales abajo
            }

            // Órdenes: si esto falla no hay base para mostrar → se propaga (la UI
            // lo presenta como error, no como "vacío").
            var orderRows = await odoo.SearchReadAsync(
                "pos.order",
                new object[] { new object[] { "session_id", "=", sessionId } },
                new string[] { "id", "amount_total" },
                500);
            var orderIds = orderRows
                .Select(o => GetInt(o, "id"))
                .Where(id => id > 0)
                .Cast<object>()
                .ToArray();

            // Pagos con tolerancia parcial: si fallan, se muestran órdenes + aviso.
            var warning = "";
            var payRows = new List<Dictionary<string, JsonElement>>();
            try
            {
                payRows = orderIds.Length == 0
                    ? new List<Dictionary<string, JsonElement>>()
                    : await odoo.SearchReadAsync(
                        "pos.payment",
                        new object[] { new object[] { "pos_order_id", "in", orderIds } },
                        new string[] { "id", "amount", "payment_method_id" },
                        500);
            }
            catch
            {
                // Fallback: payment_ids desde las órdenes y luego pos.payment por ids
                try
                {
                    var withPay = await odoo.SearchReadAsync(
                        "pos.order",
                        new object[] { new object[] { "session_id", "=", sessionId } },
                        new string[] { "id", "payment_ids" },
                        500);
                    var payIds = withPay
                        .SelectMany(o => GetIdList(GetField(o, "payment_ids")))
                        .Where(id => id > 0)
                        .Distinct()
                        .Cast<object>()
                        .ToArray();
                    if (payIds.Length > 0)
                    {
                        payRows = await odoo.SearchReadAsync(
                            "pos.payment",
                            new object[] { new object[] { "id", "in", payIds } },
                            new string[] { "id", "amount", "payment_method_id" },
                            500);
                    }
                }
                catch (Exception ex)
                {
                    warning = $"No se pudieron leer los pagos: {TrimError(ex.Message)}";
                }
            }

            var payments = payRows
                .GroupBy(p => GetSecondName(GetField(p, "payment_method_id")))
                .Select(g =>
                {
                    var first = g.First();
                    var mid = FirstId(GetField(first, "payment_method_id"));
                    return new { g, mid };
                })
                .Select(x => new PosClosingPayment(
                    x.mid,
                    x.g.Key,
                    x.g.Sum(p => GetDecimal(p, "amount")),
                    IsCash: false))
                .ToList();

            // Marca efectivo con una lectura de métodos (una sola llamada).
            // OJO: en Odoo 19 el campo es 'type' ('cash'/'bank'/'pay_later'), no 'is_cash'.
            if (payments.Count > 0)
            {
                try
                {
                    var ids = payments.Select(p => (object)p.MethodId).ToArray();
                    var mRows = await odoo.SearchReadAsync(
                        "pos.payment.method",
                        new object[] { new object[] { "id", "in", ids } },
                        new string[] { "id", "type" },
                        20);
                    var cashById = mRows.ToDictionary(GetIntM, m => GetString(m, "type") == "cash");
                    payments = payments.Select(p => p with
                    {
                        IsCash = cashById.TryGetValue(p.MethodId, out var c) && c
                    }).ToList();
                }
                catch (Exception ex)
                {
                    warning = string.IsNullOrEmpty(warning)
                        ? $"No se pudo distinguir efectivo: {TrimError(ex.Message)}"
                        : warning;
                }
            }

            return new PosClosingControlData(
                sessionId,
                orderRows.Count,
                orderRows.Sum(o => GetDecimal(o, "amount_total")),
                payments.Where(p => p.IsCash).Sum(p => p.Amount),
                payments,
                hasOfficial,
                warning);
        }

        // ── Etapa 12: Post closing detail ───────────────────────
        // Delega en el flujo oficial (post count → closing state → close_from_ui
        // con lectura de dicts). close_session_from_ui espera pares
        // [payment_method_id, diff], no el conteo: por eso no se llama directo.
        public Task PostClosingDetailsAsync(int sessionId, PosClosingDetails details)
            => odoo.ClosePosSessionAsync(sessionId, details.CashCounted, details.Notes);

        // ── Etapa 12b: Forzado (wizard Force Close Session) ────────
        // Solo cuando el cierre normal exige validación en backend
        // (descuadres históricos). Equivale al wizard del backend.
        public Task ForceCloseSessionAsync(int sessionId)
            => odoo.ForceCloseSessionAsync(sessionId);

        private static string TrimError(string message, int max = 200)
        {
            var flat = message.Replace("\r", " ").Replace("\n", " ").Trim();
            var cut = flat.IndexOf("\"debug\"", StringComparison.OrdinalIgnoreCase);
            if (cut > 0)
                flat = flat[..cut].TrimEnd().TrimEnd(',', '}', ' ');
            return flat.Length > max ? flat[..max] + "…" : flat;
        }

        // ── Helpers de parseo Odoo (many2one=[id,nombre], x2m=[ids]) ──
        private static int GetInt(Dictionary<string, JsonElement> r, string key)
            => r.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        private static int GetIntM(Dictionary<string, JsonElement> r)
            => GetInt(r, "id");

        private static bool GetBool(Dictionary<string, JsonElement> r, string key)
            => r.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

        private static string GetString(Dictionary<string, JsonElement> r, string key)
            => r.TryGetValue(key, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Number => v.ToString(),
                _ => string.Empty
            } : string.Empty;

        private static decimal GetDecimal(Dictionary<string, JsonElement> r, string key)
        {
            if (!r.TryGetValue(key, out var v)) return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0,
                JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : 0,
                _ => 0
            };
        }

        private static JsonElement GetField(Dictionary<string, JsonElement> r, string key)
            => r.TryGetValue(key, out var v) ? v : default;

        private static int FirstId(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var e in v.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number)
                        return e.GetInt32();
            return 0;
        }

        private static string GetSecondName(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Array) return string.Empty;
            var arr = v.EnumerateArray().ToArray();
            return arr.Length > 1 && arr[1].ValueKind == JsonValueKind.String ? arr[1].GetString() ?? string.Empty : string.Empty;
        }

        private static List<int> GetIdList(JsonElement v)
        {
            var list = new List<int>();
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var e in v.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number)
                        list.Add(e.GetInt32());
            return list;
        }
    }
}
