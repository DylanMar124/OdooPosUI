using System.Text.Json;
using System.Text.RegularExpressions;

namespace Odoo.WebUI.Services
{
    public class OdooService(HttpClient http, IConfiguration config)
    {
        private readonly HttpClient _http = http;
        private readonly IConfiguration _config = config;

        // Sesión del circuito Blazor Server (scoped): se rellena en AuthenticateAsync
        private string _db = string.Empty;
        private int _uid;
        private string _password = string.Empty;

        public string CurrentDb => _db;
        public int CurrentUid => _uid;

        /// <summary>
        /// Autentica contra Odoo (common/authenticate) y guarda la sesión en el servicio scoped.
        /// </summary>
        public async Task<int> AuthenticateAsync(string db, string username, string password)
        {
            var payload = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "common",
                    method = "authenticate",
                    args = new object[]
                    {
                        db,
                        username,
                        password,
                        new Dictionary<string, object>()
                    }
                },
                id = 1
            };

            var response = await _http.PostAsJsonAsync("/jsonrpc", payload);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (result.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null && err.ValueKind != JsonValueKind.Undefined)
                return 0;

            if (!result.TryGetProperty("result", out var res) || res.ValueKind == JsonValueKind.Null || res.ValueKind == JsonValueKind.False)
                return 0;

            var uid = res.GetInt32();
            if (uid > 0)
            {
                _db = db;
                _uid = uid;
                _password = password;
            }
            return uid;
        }

        /// <summary>
        /// Llamada genérica object/execute_kw search_read.
        /// Usa new object[] en todos los args para evitar CS0826.
        /// </summary>
        public async Task<List<Dictionary<string, JsonElement>>> SearchReadAsync(string model, object[] domain, string[] fields, int limit = 50, string? order = null)
        {
            EnsureAuthenticated();

            var kwargs = new Dictionary<string, object>
            {
                ["fields"] = fields,
                ["limit"] = limit
            };
            if (!string.IsNullOrEmpty(order))
                kwargs["order"] = order;

            var payload = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "object",
                    method = "execute_kw",
                    args = new object[]
                    {
                        _db,
                        _uid,
                        _password,
                        model,
                        "search_read",
                        new object[] { domain },
                        kwargs
                    }
                },
                id = 2
            };

            var response = await _http.PostAsJsonAsync("/jsonrpc", payload);
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (doc.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null && err.ValueKind != JsonValueKind.Undefined)
                throw new InvalidOperationException($"Odoo error: {err}");

            var list = new List<Dictionary<string, JsonElement>>();
            if (doc.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in res.EnumerateArray())
                {
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var prop in item.EnumerateObject())
                        dict[prop.Name] = prop.Value;
                    list.Add(dict);
                }
            }
            return list;
        }

        /// <summary>
        /// Llamada genérica object/execute_kw create. Devuelve el id creado.
        /// </summary>
        public async Task<int> CreateAsync(string model, object values)
        {
            EnsureAuthenticated();

            var payload = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "object",
                    method = "execute_kw",
                    args = new object[]
                    {
                        _db,
                        _uid,
                        _password,
                        model,
                        "create",
                        new object[] { values }
                    }
                },
                id = 3
            };

            var response = await _http.PostAsJsonAsync("/jsonrpc", payload);
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (doc.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null && err.ValueKind != JsonValueKind.Undefined)
                throw new InvalidOperationException($"Odoo error: {err}");

            return doc.GetProperty("result").GetInt32();
        }

        /// <summary>
        /// Llamada genérica object/execute_kw a cualquier método (p. ej. action_pos_session_open).
        /// methodArgs son los args posicionales del método Odoo. Usa new object[] (evita CS0826).
        /// kwargs viaja como ÚLTIMO elemento de args (convención execute_kw); así llega
        /// también 'context' (active_ids, force_close, etc.), que Odoo aplica al entorno.
        /// </summary>
        public async Task<JsonElement> ExecuteKwAsync(string model, string method, object[]? methodArgs = null, Dictionary<string, object>? kwargs = null)
        {
            EnsureAuthenticated();

            var callArgs = new List<object>
            {
                _db,
                _uid,
                _password,
                model,
                method,
                methodArgs ?? new object[0]
            };
            if (kwargs is not null)
                callArgs.Add(kwargs);

            var payload = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "object",
                    method = "execute_kw",
                    args = callArgs.ToArray()
                },
                id = 4
            };

            var response = await _http.PostAsJsonAsync("/jsonrpc", payload);
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (doc.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null && err.ValueKind != JsonValueKind.Undefined)
                throw new InvalidOperationException($"Odoo error: {err}");

            return doc.GetProperty("result");
        }

        private static Dictionary<string, object> Ctx(int sessionId, bool forceClose = false) => new()
        {
            ["context"] = new Dictionary<string, object>
            {
                ["active_model"] = "pos.session",
                ["active_id"] = sessionId,
                ["active_ids"] = new object[] { sessionId },
                ["force_close"] = forceClose,
                ["cash_control"] = false
            }
        };

        /// <summary>
        /// Puntos de venta disponibles (pos.config) para el selector de "Abrir caja".
        /// </summary>
        public Task<List<Dictionary<string, JsonElement>>> GetPosConfigsAsync()
            => SearchReadAsync(
                "pos.config",
                new object[0],
                new string[] { "id", "name" },
                20);

        /// <summary>
        /// Abre una sesión de caja (pos.session) en el config indicado con su efectivo inicial.
        /// Flujo real Odoo (el CashOpeningPopup del frontend): create({config_id}) ->
        /// _set_opening_control_data(efectivo, notas), que es quien pone state='opened'.
        /// action_pos_session_open SOLO prepara el balance inicial, no abre.
        /// Éxito = estado 'opened' verificado; si no, se lanza error con el detalle.
        /// </summary>
        public async Task<int> OpenPosSessionAsync(int configId, decimal openingCash = 0, string? notes = null)
        {
            var newId = await CreateAsync(
                "pos.session",
                new Dictionary<string, object> { ["config_id"] = configId });

            var cashError = await TrySetOpeningControlAsync(newId, openingCash, notes);

            Exception? openEx = null;
            try
            {
                await ExecuteKwAsync("pos.session", "action_pos_session_open", new object[] { new object[] { newId } });
            }
            catch (Exception ex)
            {
                openEx = ex;
            }

            var state = await GetSessionStateAsync(newId);
            if (state == "opened")
                return newId;

            throw new InvalidOperationException(
                $"La caja #{newId} quedó en '{state ?? "desconocido"}' (no pasó el opening control). " +
                $"Control de apertura: {cashError ?? "ok"}. Apertura: {openEx?.Message ?? "ok"}.");
        }

        /// <summary>
        /// Inicia una caja creada pero pendiente (opening_control): registra el efectivo
        /// inicial como en el Odoo original y la pasa a 'opened' (verificado).
        /// </summary>
        public async Task<int> StartPosSessionAsync(int sessionId, decimal openingCash, string? notes = null)
        {
            var cashError = await TrySetOpeningControlAsync(sessionId, openingCash, notes);

            Exception? openEx = null;
            try
            {
                await ExecuteKwAsync("pos.session", "action_pos_session_open", new object[] { new object[] { sessionId } });
            }
            catch (Exception ex)
            {
                openEx = ex;
            }

            var state = await GetSessionStateAsync(sessionId);
            if (state == "opened")
                return sessionId;

            throw new InvalidOperationException(
                $"La caja #{sessionId} quedó en '{state ?? "desconocido"}' (no pasó el opening control). " +
                $"Control de apertura: {cashError ?? "ok"}. Apertura: {openEx?.Message ?? "ok"}.");
        }

        /// <summary>
        /// Cierra la caja con el flujo oficial del frontend (ClosePosPopup):
        /// post_closing_cash_details → update_closing_control_state_session →
        /// close_session_from_ui. Esos métodos devuelven dicts {'successful': bool,
        /// 'message', 'redirect'} en vez de lanzar: aquí SÍ se leen, así el motivo
        /// real (borradores, diferencias, backend) siempre llega a la UI.
        /// </summary>
        public async Task ClosePosSessionAsync(int sessionId, decimal closingCash = 0, string? notes = null)
        {
            string? lastDetail = null;

            // 1) Postea el conteo de efectivo (devuelve dict, no lanza)
            try
            {
                var post = await ExecuteKwAsync(
                    "pos.session",
                    "post_closing_cash_details",
                    new object[] { new object[] { sessionId }, closingCash });
                lastDetail = CheckClosingResult(post) ?? lastDetail;
            }
            catch (Exception ex)
            {
                lastDetail = ShortOdooError(ex.Message);
            }

            if (await IsSessionClosedAsync(sessionId))
            {
                await EnsureStopAtAsync(sessionId);
                return;
            }

            // 2) Marca control de cierre con notas (como el frontend)
            try
            {
                await ExecuteKwAsync(
                    "pos.session",
                    "update_closing_control_state_session",
                    new object[] { new object[] { sessionId }, notes ?? string.Empty });
            }
            catch (Exception ex)
            {
                lastDetail = ShortOdooError(ex.Message);
            }

            // 3) Cierre oficial: devuelve {'successful': True} o el motivo + redirect
            try
            {
                var result = await ExecuteKwAsync(
                    "pos.session",
                    "close_session_from_ui",
                    new object[] { new object[] { sessionId }, new object[0] });
                var msg = CheckClosingResult(result);
                if (msg is not null)
                    throw new InvalidOperationException(msg);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastDetail = ShortOdooError(ex.Message);
            }

            if (await IsSessionClosedAsync(sessionId))
            {
                await EnsureStopAtAsync(sessionId);
                return;
            }

            // 4) Fallback clásico para versiones sin los métodos UI
            try
            {
                var r = await ExecuteKwAsync("pos.session", "action_pos_session_closing_control", new object[] { new object[] { sessionId } });
                lastDetail = CheckClosingResult(r) ?? lastDetail;
            }
            catch (Exception ex)
            {
                lastDetail = ShortOdooError(ex.Message);
            }

            try
            {
                var r = await ExecuteKwAsync("pos.session", "action_pos_session_close", new object[] { new object[] { sessionId } });
                lastDetail = CheckClosingResult(r) ?? lastDetail;
            }
            catch (Exception ex)
            {
                lastDetail = ShortOdooError(ex.Message);
            }

            if (!await IsSessionClosedAsync(sessionId))
                throw new InvalidOperationException(
                    $"No se pudo cerrar la caja #{sessionId}. Odoo dice: {lastDetail ?? "motivo no informado; revisa órdenes en borrador o diferencias de efectivo"}. " +
                    "Si pide ir al backend: Punto de Venta → Pedidos → Sesiones → validar diferencias.");

            // Blindaje: el tablero de Odoo (pos_config._compute_last_session) se estrella
            // con AttributeError si la sesión cerrada queda sin stop_at. Garantízalo.
            await EnsureStopAtAsync(sessionId);
        }

        /// <summary>
        /// Interpreta los dicts de cierre: {'successful': True} → null (ok);
        /// {'successful': False, 'message', 'redirect'} → mensaje accionable.
        /// </summary>
        private static string? CheckClosingResult(JsonElement result)
        {
            if (result.ValueKind != JsonValueKind.Object)
                return null;
            // Wizard de forzado: Odoo exige validar el descuadre en backend
            if (WizardIdFrom(result) is not null)
                return "Odoo exige el wizard 'Force Close Session' (descuadre contable). Usa 'Forzar cierre'.";
            if (!result.TryGetProperty("successful", out var s))
                return null; // otro retorno: se verifica estado fuera
            if (s.ValueKind == JsonValueKind.True)
                return null;
            var msg = result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : "Odoo rechazó el cierre.";
            var redirect = result.TryGetProperty("redirect", out var r) && r.ValueKind == JsonValueKind.True;
            return msg + (redirect
                ? " (Odoo pide validarlo en el backend: Punto de Venta → Pedidos → Sesiones)."
                : "");
        }

        /// <summary>
        /// Forzado de cierre (equivale al botón del wizard "Force Close Session"):
        /// provoca el wizard (devuelve res_model/res_id), lee el descuadre
        /// (amount_to_balance) y la cuenta que el propio Odoo calcula, y reintenta
        /// el cierre con balanceo explícito. Solo para sesiones cuyo cierre normal
        /// exige validación en backend. Nunca inventa montos ni cuentas.
        /// </summary>
        public async Task ForceCloseSessionAsync(int sessionId)
        {
            // A) El cierre normal a veces basta tras reintentar (conteo ya posteado)
            try { await ClosePosSessionAsync(sessionId); }
            catch { /* seguimos al forzado */ }
            if (await IsSessionClosedAsync(sessionId))
            {
                await EnsureStopAtAsync(sessionId);
                return;
            }

            // B) Provoca el wizard y captura su id desde el dict de respuesta
            int wizardId;
            try
            {
                var r = await ExecuteKwAsync(
                    "pos.session",
                    "action_pos_session_close",
                    new object[] { new object[] { sessionId } });
                wizardId = WizardIdFrom(r) ?? 0;
                if (wizardId <= 0)
                    throw new InvalidOperationException(
                        "Odoo no devolvió el wizard de forzado (¿la sesión tiene borradores o ya se cerró?).");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"No se pudo provocar el wizard de forzado: {ShortOdooError(ex.Message)}");
            }

            var wizRows = await SearchReadAsync(
                "pos.close.session.wizard",
                new object[] { new object[] { "id", "=", wizardId } },
                new string[] { "id", "amount_to_balance", "account_id" },
                1);
            if (wizRows.Count == 0)
                throw new InvalidOperationException($"Wizard #{wizardId} no encontrado.");
            var amount = wizRows[0].TryGetValue("amount_to_balance", out var av)
                && av.ValueKind == JsonValueKind.Number && av.TryGetDecimal(out var ad) ? ad : 0m;
            var accountId = M2oId(wizRows[0], "account_id");
            if (accountId <= 0)
                throw new InvalidOperationException(
                    "El wizard no trae cuenta de descuadre. Complétalo en Odoo: Punto de Venta → Pedidos → Sesiones.");

            // C) Balanceo directo con ids (sonda: el servidor suele esperar el record,
            // no el id, así que puede fallar con "'int' object has no attribute 'id'").
            // No se aborta aquí: la vía real es el wizard del paso D.
            string? forceError = null;
            try
            {
                await ExecuteKwAsync(
                    "pos.session",
                    "action_pos_session_close",
                    new object[] { new object[] { sessionId }, accountId, amount });
            }
            catch (Exception ex)
            {
                forceError = ShortOdooError(ex.Message);
            }

            if (!await IsSessionClosedAsync(sessionId))
            {
                // D) Vía wizard con valores (receta del foro): el wizard pasa el record
                // de cuenta internamente, así que no sufre el problema del paso C.
                try
                {
                    var wiz2 = await ExecuteKwAsync(
                        "pos.close.session.wizard",
                        "create",
                        new object[] { new Dictionary<string, object> { ["amount_to_balance"] = amount, ["account_id"] = accountId } });
                    if (wiz2.ValueKind != JsonValueKind.Number)
                        throw new InvalidOperationException($"El wizard no devolvió id. Respuesta: {wiz2}");

                    await ExecuteKwAsync(
                        "pos.close.session.wizard",
                        "close_session",
                        new object[] { new object[] { wiz2.GetInt32() } },
                        Ctx(sessionId));
                }
                catch (Exception ex)
                {
                    forceError = (forceError is null ? "" : forceError + " | ") + ShortOdooError(ex.Message);
                }
            }

            if (!await IsSessionClosedAsync(sessionId))
                throw new InvalidOperationException(
                    $"Forzado de cierre falló: {forceError ?? "sin detalle"}. " +
                    "Ciérrala en Odoo: Punto de Venta → Pedidos → Sesiones (el wizard mostrará monto y cuenta).");

            await EnsureStopAtAsync(sessionId);
        }

        private static int? WizardIdFrom(JsonElement result)
        {
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("res_model", out var m) && m.ValueKind == JsonValueKind.String
                && m.GetString() == "pos.close.session.wizard"
                && result.TryGetProperty("res_id", out var id) && id.ValueKind == JsonValueKind.Number)
                return id.GetInt32();
            return null;
        }

        /// <summary>
        /// Control de apertura: registra efectivo inicial + notas y pasa a 'opened'.
        /// Método público que usa el frontend oficial: set_opening_control(cashbox_value, notes).
        /// (OJO: _set_opening_control_data existe pero es privado y Odoo lo bloquea por RPC;
        /// set_cashbox_pos solo existe en versiones viejas y se mantiene como fallback.)
        /// Devuelve null si alguno funcionó, o el detalle de TODOS los intentos si ninguno existe.
        /// </summary>
        private async Task<string?> TrySetOpeningControlAsync(int sessionId, decimal cash, string? notes)
        {
            var errors = new List<string>();
            foreach (var m in new string[] { "set_opening_control", "set_cashbox_pos" })
            {
                try
                {
                    await ExecuteKwAsync(
                        "pos.session",
                        m,
                        new object[] { new object[] { sessionId }, cash, notes ?? string.Empty });
                    return null;
                }
                catch (Exception ex)
                {
                    errors.Add($"{m} → {ShortOdooError(ex.Message)}");
                }
            }
            return string.Join(" | ", errors);
        }

        private static string ShortOdooError(string message, int max = 300)
        {
            var flat = message.Replace("\r", " ").Replace("\n", " ").Trim();
            // Recorta el traceback: lo útil va al inicio (nombre + message de Odoo)
            var cut = flat.IndexOf("\"debug\"", StringComparison.OrdinalIgnoreCase);
            if (cut > 0)
                flat = flat[..cut].TrimEnd().TrimEnd(',', '}', ' ');
            return flat.Length > max ? flat[..max] + "…" : flat;
        }

        private async Task<string?> GetSessionStateAsync(int sessionId)
        {
            var rows = await SearchReadAsync(
                "pos.session",
                new object[] { new object[] { "id", "=", sessionId } },
                new string[] { "id", "state" },
                1);
            return rows.Count > 0 && rows[0].TryGetValue("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
        }

        /// <summary>
        /// Deja stop_at informado en la sesión cerrada. Best-effort: si Odoo bloquea
        /// el write, se ignora (el tablero se repara con el parche de pos_config.py).
        /// </summary>
        private async Task EnsureStopAtAsync(int sessionId)
        {
            try
            {
                var rows = await SearchReadAsync(
                    "pos.session",
                    new object[] { new object[] { "id", "=", sessionId } },
                    new string[] { "id", "stop_at" },
                    1);
                var empty = rows.Count == 0
                    || !rows[0].TryGetValue("stop_at", out var st)
                    || st.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(st.GetString());
                if (empty)
                {
                    await ExecuteKwAsync(
                        "pos.session",
                        "write",
                        new object[]
                        {
                            new object[] { sessionId },
                            new Dictionary<string, object>
                            {
                                ["stop_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                            }
                        });
                }
            }
            catch
            {
                // No crítico para el flujo de caja
            }
        }

        private async Task<bool> IsSessionClosedAsync(int sessionId)
            => await GetSessionStateAsync(sessionId) == "closed";

        private async Task<bool> IsSessionClosedOrClosingAsync(int sessionId)
            => await GetSessionStateAsync(sessionId) is "closed" or "closing_control";

        /// <summary>
        /// Restaura una sesión previamente validada (p. ej. tras F5 con credenciales
        /// persistidas por OdooAuthState). Evita un roundtrip de re-autenticación.
        /// </summary>
        public void SetSession(string db, int uid, string password)
        {
            _db = db;
            _uid = uid;
            _password = password;
        }

        public void ClearSession()
        {
            _db = string.Empty;
            _uid = 0;
            _password = string.Empty;
        }

        /// <summary>
        /// Sesión POS no cerrada con su estado: 'opened', 'opening_control' o
        /// 'closing_control' (cierre atorado a medio validar: bloquea abrir otra caja
        /// y hay que terminarlo). Null solo si no hay ninguna. Las utilizables
        /// siempre tienen prioridad sobre las atoradas en cierre.
        /// </summary>
        public async Task<PosSessionInfo?> GetActiveSessionAsync()
        {
            var rows = await SearchReadAsync(
                "pos.session",
                new object[] { new object[] { "state", "in", new object[] { "opening_control", "opened", "closing_control" } } },
                new string[] { "id", "name", "config_id", "user_id", "state" },
                10);

            PosSessionInfo? best = null;
            var bestRank = -2;
            foreach (var r in rows)
            {
                if (!r.TryGetValue("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                var info = new PosSessionInfo
                {
                    Id = idEl.GetInt32(),
                    Name = r.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty,
                    State = r.TryGetValue("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty,
                    ConfigId = FirstInt(r.TryGetValue("config_id", out var c) ? c : default)
                };
                var mine = r.TryGetValue("user_id", out var u) && u.ValueKind == JsonValueKind.Array
                    && u.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Number && e.GetInt32() == _uid);

                // rank: mía+abierta (3) > mía+apertura (2) > abierta (1) > apertura (0) > cierre (-1)
                var rank = info.IsClosing ? -1 : (mine ? 2 : 0) + (info.IsOpened ? 1 : 0);
                if (best is null || rank > bestRank || (rank == bestRank && info.Id > best.Id))
                {
                    bestRank = rank;
                    best = info;
                }
            }

            return best;
        }

        /// <summary>
        /// Id de la sesión utilizable o 0 si la caja está cerrada.
        /// </summary>
        public async Task<int> GetOpenPosSessionIdAsync()
        {
            var info = await GetActiveSessionAsync();
            return info?.Id ?? 0;
        }

        /// <summary>
        /// Sesión no cerrada de un punto de venta concreto (cualquier estado útil:
        /// opening_control / opened / closing_control). Null si no tiene ninguna.
        /// Sirve para no intentar crear cuando ese PDV ya tiene caja abierta
        /// (Odoo lo rechaza con "Another session is already opened").
        /// </summary>
        public async Task<PosSessionInfo?> GetSessionForConfigAsync(int configId)
        {
            var rows = await SearchReadAsync(
                "pos.session",
                new object[] { new object[] { "config_id", "=", configId }, new object[] { "state", "in", new object[] { "opening_control", "opened", "closing_control" } } },
                new string[] { "id", "name", "config_id", "user_id", "state" },
                1);
            if (rows.Count == 0)
                return null;

            var r = rows[0];
            return new PosSessionInfo
            {
                Id = r.TryGetValue("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0,
                Name = r.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty,
                State = r.TryGetValue("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty,
                ConfigId = configId
            };
        }

        /// <summary>
        /// Devuelve la sesión POS utilizable o la crea si la caja está cerrada.
        /// Si no indicas configId, usa el primer pos.config disponible.
        /// </summary>
        public async Task<int> GetOrCreateSessionAsync(int? configId = null)
        {
            var openId = await GetOpenPosSessionIdAsync();
            if (openId > 0)
                return openId;

            var cfg = configId ?? 0;
            if (cfg <= 0)
            {
                var configs = await GetPosConfigsAsync();
                foreach (var c in configs)
                {
                    if (c.TryGetValue("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        cfg = idEl.GetInt32();
                        break;
                    }
                }
            }

            if (cfg <= 0)
                throw new InvalidOperationException("No hay puntos de venta (pos.config) en Odoo para abrir la caja.");

            return await OpenPosSessionAsync(cfg);
        }

        /// <summary>
        /// Crea la venta por el pipeline oficial del POS (Odoo 18/19): pos.order.sync_from_ui.
        /// Impuestos REALES por línea (tax_ids del producto + subtotales excl/incl): así el
        /// asiento contable cuadra y el cierre no pide forzado por descuadre fiscal.
        /// Totales calculados aquí (misma regla que la UI): amount_total = Σ incl,
        /// amount_tax = Σ tax, amount_paid = total. Sin MudBlazor, .NET 10.
        /// </summary>
        public async Task<int> CrearDesdeUiAsync(int sessionId, List<CartItem> carrito, decimal total, int? paymentMethodId = null, bool toInvoice = false, int? partnerId = null)
        {
            if (sessionId <= 0)
                throw new ArgumentException("Sesión POS inválida.", nameof(sessionId));
            if (carrito is null || carrito.Count == 0)
                throw new ArgumentException("El carrito está vacío.", nameof(carrito));
            if (total <= 0)
                throw new ArgumentException("El total debe ser mayor que 0.", nameof(total));

            var pmId = paymentMethodId ?? await ResolvePaymentMethodIdAsync(sessionId);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var reference = $"WebUI-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}";

            var lines = new object[carrito.Count];
            decimal computedTax = 0m, computedTotal = 0m;
            for (var i = 0; i < carrito.Count; i++)
            {
                var l = carrito[i];
                var lineTax = Math.Round(l.LineTotal * l.TaxRate, 2);
                var lineIncl = l.LineTotal + lineTax;
                computedTax += lineTax;
                computedTotal += lineIncl;
                lines[i] = new object[] { 0, 0, new Dictionary<string, object>
                {
                    ["uuid"] = Guid.NewGuid().ToString(),
                    ["product_id"] = l.ProductId,
                    ["qty"] = l.Qty,
                    ["price_unit"] = l.UnitPrice,
                    ["discount"] = 0,
                    ["tax_ids"] = new object[] { new object[] { 6, 0, l.TaxIds.Cast<object>().ToArray() } },
                    ["price_subtotal"] = l.LineTotal,
                    ["price_subtotal_incl"] = lineIncl,
                    ["full_product_name"] = l.Name ?? string.Empty,
                    ["name"] = l.Name ?? string.Empty
                } };
            }

            var payments = new object[]
            {
                new object[] { 0, 0, new Dictionary<string, object>
                {
                    ["uuid"] = Guid.NewGuid().ToString(),
                    ["amount"] = computedTotal,
                    ["payment_method_id"] = pmId,
                    ["name"] = now
                } }
            };

            var order = new Dictionary<string, object>
            {
                ["uuid"] = Guid.NewGuid().ToString(),
                ["access_token"] = Guid.NewGuid().ToString("N"),
                ["name"] = reference,
                ["pos_reference"] = reference,
                ["session_id"] = sessionId,
                ["user_id"] = _uid,
                ["partner_id"] = partnerId is > 0 ? partnerId.Value : (object)false,
                ["fiscal_position_id"] = false,
                ["date_order"] = now,
                ["state"] = "paid",
                ["to_invoice"] = toInvoice,
                ["amount_tax"] = computedTax,
                ["amount_total"] = computedTotal,
                ["amount_paid"] = computedTotal,
                ["amount_return"] = 0m,
                ["last_order_preparation_change"] = "{}",
                ["lines"] = lines,
                ["payment_ids"] = payments
            };

            JsonElement result;
            try
            {
                result = await ExecuteKwAsync(
                    "pos.order",
                    "sync_from_ui",
                    new object[] { new object[] { order } });
            }
            catch (Exception ex) when (IsPickingSequenceConflict(ex))
            {
                // La secuencia del tipo de operación va atrasada (p. ej. WH/POS/00001 ya
                // existe): se adelanta al último albarán + 1 y se reintenta UNA vez.
                // El intento fallido hizo rollback total, así que el reintento es seguro.
                try
                {
                    var nextNumber = await RepairPickingSequenceAsync(sessionId);
                    result = await ExecuteKwAsync(
                        "pos.order",
                        "sync_from_ui",
                        new object[] { new object[] { order } });
                    System.Diagnostics.Debug.WriteLine($"Secuencia de albaranes reparada (siguiente: {nextNumber}).");
                }
                catch (Exception repairEx)
                {
                    throw new InvalidOperationException(
                        $"Conflicto de secuencia de albaranes y la reparación automática falló: {ShortOdooError(repairEx.Message)}. " +
                        "En Odoo: Inventario → Configuración → Secuencias → sube el 'Siguiente número' por encima del último albarán existente. " +
                        $"Detalle original: {ShortOdooError(ex.Message)}");
                }
            }

            var orderId = ExtractSyncOrderId(result);
            if (orderId <= 0)
                throw new InvalidOperationException($"Odoo no devolvió id de orden. Respuesta: {result}");

            return orderId;
        }

        private static bool IsPickingSequenceConflict(Exception ex)
            => ex.Message.Contains("stock_picking_name_uniq", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Adelanta la secuencia del tipo de operación del POS al último albarán + 1.
        /// Cadena: sesión → config → picking_type_id → sequence_id → ir.sequence.
        /// Devuelve el nuevo "siguiente número".
        /// </summary>
        public async Task<int> RepairPickingSequenceAsync(int sessionId)
        {
            var sessRows = await SearchReadAsync(
                "pos.session",
                new object[] { new object[] { "id", "=", sessionId } },
                new string[] { "id", "config_id" },
                1);
            if (sessRows.Count == 0)
                throw new InvalidOperationException($"Sesión #{sessionId} no encontrada.");
            var configId = M2oId(sessRows[0], "config_id");

            var cfgRows = await SearchReadAsync(
                "pos.config",
                new object[] { new object[] { "id", "=", configId } },
                new string[] { "id", "picking_type_id" },
                1);
            var pickingTypeId = cfgRows.Count > 0 ? M2oId(cfgRows[0], "picking_type_id") : 0;
            if (pickingTypeId <= 0)
                throw new InvalidOperationException("El POS no tiene tipo de operación de inventario (picking_type_id). Revísalo en Inventario.");

            var ptRows = await SearchReadAsync(
                "stock.picking.type",
                new object[] { new object[] { "id", "=", pickingTypeId } },
                new string[] { "id", "sequence_id" },
                1);
            var seqId = ptRows.Count > 0 ? M2oId(ptRows[0], "sequence_id") : 0;
            if (seqId <= 0)
                throw new InvalidOperationException("El tipo de operación no tiene secuencia asignada.");

            var seqRows = await SearchReadAsync(
                "ir.sequence",
                new object[] { new object[] { "id", "=", seqId } },
                new string[] { "id", "prefix", "number_next_actual" },
                1);
            if (seqRows.Count == 0)
                throw new InvalidOperationException($"Secuencia #{seqId} no encontrada.");
            var prefix = seqRows[0].TryGetValue("prefix", out var pfx) && pfx.ValueKind == JsonValueKind.String
                ? pfx.GetString() ?? string.Empty
                : string.Empty;
            var next = seqRows[0].TryGetValue("number_next_actual", out var nxt) && nxt.ValueKind == JsonValueKind.Number
                ? nxt.GetInt32()
                : 1;
            if (string.IsNullOrEmpty(prefix))
                throw new InvalidOperationException("La secuencia no tiene prefijo para localizar albaranes.");

            var pickRows = await SearchReadAsync(
                "stock.picking",
                new object[] { new object[] { "name", "ilike", prefix } },
                new string[] { "id", "name" },
                200,
                "id desc");
            var max = 0;
            var rx = new Regex("^" + Regex.Escape(prefix) + @"(\d+)");
            foreach (var pr in pickRows)
            {
                if (!pr.TryGetValue("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                    continue;
                var m = rx.Match(nm.GetString() ?? string.Empty);
                if (m.Success && int.TryParse(m.Groups[1].Value.TrimStart('0'), out var n))
                    max = Math.Max(max, n);
            }

            if (max < next)
                return next; // la secuencia ya está bien; el conflicto vino de otro lado

            var repairedNext = max + 1;
            try
            {
                await ExecuteKwAsync(
                    "ir.sequence",
                    "write",
                    new object[] { new object[] { seqId }, new Dictionary<string, object> { ["number_next_actual"] = repairedNext } });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Sin permiso para ajustar la secuencia #{seqId} (se necesita acceso a Inventario/Secuencias). {ShortOdooError(ex.Message)}");
            }
            return repairedNext;
        }

        /// <summary>Id de un many2one ([id, nombre]), número directo o 0.</summary>
        private static int M2oId(Dictionary<string, JsonElement> row, string key)
        {
            if (!row.TryGetValue(key, out var v))
                return 0;
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var e in v.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number)
                        return e.GetInt32();
            return 0;
        }

        /// <summary>
        /// sync_from_ui responde {"pos.order": [{"id": N, ...}], ...}.
        /// Extrae ese id; si la forma varía, cae al extractor genérico.
        /// </summary>
        private static int ExtractSyncOrderId(JsonElement result)
        {
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("pos.order", out var arr)
                && arr.ValueKind == JsonValueKind.Array
                && arr.GetArrayLength() > 0
                && arr[0].ValueKind == JsonValueKind.Object
                && arr[0].TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.Number)
                return idEl.GetInt32();

            return ExtractFirstId(result);
        }

        /// <summary>
        /// Resuelve el método de pago a usar: el primero del pos.config de la sesión.
        /// Si el config no tiene, cae al primer pos.payment.method global.
        /// </summary>
        public async Task<int> ResolvePaymentMethodIdAsync(int sessionId)
        {
            var sessRows = await SearchReadAsync(
                "pos.session",
                new object[] { new object[] { "id", "=", sessionId } },
                new string[] { "id", "config_id" },
                1);

            var configId = 0;
            if (sessRows.Count > 0 && sessRows[0].TryGetValue("config_id", out var cfg))
                configId = FirstInt(cfg);

            if (configId > 0)
            {
                var cfgRows = await SearchReadAsync(
                    "pos.config",
                    new object[] { new object[] { "id", "=", configId } },
                    new string[] { "id", "payment_method_ids" },
                    1);
                if (cfgRows.Count > 0 && cfgRows[0].TryGetValue("payment_method_ids", out var pm))
                {
                    var id = FirstInt(pm);
                    if (id > 0)
                        return id;
                }
            }

            // Fallback: cualquier método de pago POS existente
            var anyRows = await SearchReadAsync(
                "pos.payment.method",
                new object[0],
                new string[] { "id", "name" },
                1);
            if (anyRows.Count > 0 && anyRows[0].TryGetValue("id", out var anyId)
                && anyId.ValueKind == JsonValueKind.Number)
                return anyId.GetInt32();

            throw new InvalidOperationException(
                "La caja no tiene métodos de pago. Configúralos en Odoo: Punto de Venta → Configuración → Punto de venta → Pagos.");
        }

        /// <summary>Primer entero de un valor Odoo (número, [id, nombre] o lista de ids).</summary>
        private static int FirstInt(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.GetInt32();
                case JsonValueKind.Array:
                    foreach (var el in v.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Number)
                            return el.GetInt32();
                    }
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Los métodos de sincronización devuelven formas distintas según versión.
        /// Extrae el primer entero positivo de cualquier forma.
        /// </summary>
        private static int ExtractFirstId(JsonElement result)
        {
            switch (result.ValueKind)
            {
                case JsonValueKind.Number:
                    return result.GetInt32();
                case JsonValueKind.String:
                    return int.TryParse(result.GetString(), out var s) ? s : 0;
                case JsonValueKind.Array:
                    foreach (var el in result.EnumerateArray())
                    {
                        var id = ExtractFirstId(el);
                        if (id > 0)
                            return id;
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var p in result.EnumerateObject())
                    {
                        var id = ExtractFirstId(p.Value);
                        if (id > 0)
                            return id;
                    }
                    break;
            }
            return 0;
        }

        private void EnsureAuthenticated()
        {
            if (_uid <= 0 || string.IsNullOrEmpty(_db))
                throw new InvalidOperationException("Sin sesión Odoo. Llama primero a AuthenticateAsync(db, username, password).");
        }

        public async Task<int> LoginAsync()
        {
            var payload = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "common",
                    method = "authenticate",
                    // Aquí está el fix: object[] en lugar de new[]
                    args = new object[]
                    {
                        _config["Odoo:Db"]!,
                        _config["Odoo:Username"]!,
                        _config["Odoo:Password"]!,
                        new Dictionary<string, object>()
                    }
                },
                id = 1
            };
            var response = await _http.PostAsJsonAsync("/jsonrpc", payload);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.GetProperty("result").GetInt32(); // te regresa el uid
        }
    }
}
