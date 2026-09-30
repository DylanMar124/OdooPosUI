using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Odoo.WebUI.Services
{
    /// <summary>
    /// Estado de autenticación Odoo que sobrevive a recargas de página.
    /// OdooService es scoped al circuito Blazor: al recargar (F5) se crea un circuito
    /// nuevo y se pierde _uid. Aquí persistimos las credenciales en el navegador
    /// (cifradas por Data Protection) y las restauramos en el circuito nuevo.
    /// </summary>
    public class OdooAuthState(
        OdooService odoo,
        ProtectedSessionStorage sessionStorage,
        ProtectedLocalStorage localStorage,
        IConfiguration config)
    {
        private const string SessionKey = "odoo.auth.session";
        private const string RememberKey = "odoo.auth.remember";

        private readonly OdooService _odoo = odoo;
        private readonly ProtectedSessionStorage _session = sessionStorage;
        private readonly ProtectedLocalStorage _local = localStorage;
        private readonly IConfiguration _config = config;

        private bool _restored;

        public string Db { get; private set; } = string.Empty;
        public string Username { get; private set; } = string.Empty;
        public int Uid { get; private set; }
        public bool IsAuthenticated => Uid > 0;

        public string DefaultDb => _config["Odoo:Db"] ?? string.Empty;
        public string DefaultUsername => _config["Odoo:Username"] ?? string.Empty;

        public async Task<int> LoginAsync(string db, string username, string password, bool rememberMe)
        {
            var uid = await _odoo.AuthenticateAsync(db, username, password);
            if (uid <= 0)
                return 0;

            Db = db;
            Username = username;
            Uid = uid;
            _restored = true;

            var record = new AuthRecord(db, username, password, uid);
            await _session.SetAsync(SessionKey, record);
            if (rememberMe)
                await _local.SetAsync(RememberKey, record);
            else
                await _local.DeleteAsync(RememberKey);

            return uid;
        }

        /// <summary>
        /// Restaura la sesión en un circuito nuevo (p. ej. tras F5). Seguro de llamar
        /// durante prerender: si JS interop no está disponible, devuelve false.
        /// </summary>
        public async Task<bool> TryRestoreAsync()
        {
            if (IsAuthenticated)
                return true;

            if (!_restored)
            {
                _restored = true;
                if (await TryRestoreFrom(_session, SessionKey))
                    return true;
                if (await TryRestoreFrom(_local, RememberKey))
                    return true;
            }
            return IsAuthenticated;
        }

        /// <summary>Identidad guardada para prellenar el formulario (sin validar sesión).</summary>
        public async Task<(string Db, string Username)?> GetSavedIdentityAsync()
        {
            try
            {
                var session = await _session.GetAsync<AuthRecord>(SessionKey);
                if (session.Success && session.Value is not null && !string.IsNullOrEmpty(session.Value.Db))
                    return (session.Value.Db, session.Value.Username);
                var local = await _local.GetAsync<AuthRecord>(RememberKey);
                if (local.Success && local.Value is not null && !string.IsNullOrEmpty(local.Value.Db))
                    return (local.Value.Db, local.Value.Username);
            }
            catch (InvalidOperationException)
            {
                // Prerender: JS interop aún no disponible
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Datos manipulados: se ignoran
            }
            return null;
        }

        public async Task LogoutAsync()
        {
            Db = string.Empty;
            Username = string.Empty;
            Uid = 0;
            _odoo.ClearSession();
            try
            {
                await _session.DeleteAsync(SessionKey);
                await _local.DeleteAsync(RememberKey);
            }
            catch (InvalidOperationException)
            {
                // Prerender: nada que limpiar aún
            }
        }

        private async Task<bool> TryRestoreFrom<TStorage>(TStorage storage, string key)
            where TStorage : class
        {
            try
            {
                var result = storage switch
                {
                    ProtectedSessionStorage s => await s.GetAsync<AuthRecord>(key),
                    ProtectedLocalStorage l => await l.GetAsync<AuthRecord>(key),
                    _ => default
                };
                var record = result.Value;
                if (!result.Success || record is null || record.Uid <= 0
                    || string.IsNullOrEmpty(record.Db) || string.IsNullOrEmpty(record.Password))
                    return false;

                // Restauración directa sin roundtrip: el login ya validó estas credenciales.
                // Si el password cambió en Odoo, la próxima llamada fallará y el guard
                // de Pos redirigirá al login.
                _odoo.SetSession(record.Db, record.Uid, record.Password);
                Db = record.Db;
                Username = record.Username;
                Uid = record.Uid;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // prerender sin JS interop
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false; // payload manipulado
            }
        }

        private sealed record AuthRecord(string Db, string Username, string Password, int Uid);
    }
}
