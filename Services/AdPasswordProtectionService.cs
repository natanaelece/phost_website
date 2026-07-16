using System;
using Microsoft.AspNetCore.DataProtection;

namespace PremierAPI.Services
{
    public sealed class AdPasswordProtectionService
    {
        private readonly IDataProtector _protector;

        public AdPasswordProtectionService(IDataProtectionProvider provider)
        {
            _protector = provider.CreateProtector("PremierAPI.PendingAdPassword.v1");
        }

        public string Protect(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Senha obrigatória para preparar o provisionamento AD.", nameof(password));

            return _protector.Protect(password);
        }

        public string Unprotect(string protectedPassword)
        {
            if (string.IsNullOrWhiteSpace(protectedPassword))
                throw new InvalidOperationException("Senha protegida do provisionamento AD não encontrada.");

            return _protector.Unprotect(protectedPassword);
        }
    }
}
