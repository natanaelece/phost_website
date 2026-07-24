using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Novell.Directory.Ldap;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace PremierAPI.Services
{
    public class AdUserDto
    {
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public bool IsActive { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<string> Groups { get; set; } = new List<string>();
        public List<string> Computers { get; set; } = new List<string>();
        public string OuPath { get; set; } = ""; // "USUARIOS" ou "USUARIOS_EXPIRADOS"
        public bool AllowAllComputers { get; set; } // true quando userWorkstations está vazio no AD (sem restrição)
        public long? UserAccountControl { get; set; }
        public string Email { get; set; } = "";
        public string TelephoneNumber { get; set; } = "";
        public bool PasswordNeverExpires => UserAccountControl.HasValue && (UserAccountControl.Value & 65536) != 0;
    }

    public class AdGroupDto
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public class AdComputerDto
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public bool IsActive { get; set; }
        public List<string> Groups { get; set; } = new List<string>();
    }

    public class ComputerGroupSelectionRequiredException : Exception
    {
        public string ComputerName { get; }
        public string? SuggestedGroup { get; }
        public string Operation { get; }

        public ComputerGroupSelectionRequiredException(string computerName, string? suggestedGroup, string operation)
            : base($"Selecione manualmente o grupo do computador '{computerName}'.")
        {
            ComputerName = computerName;
            SuggestedGroup = suggestedGroup;
            Operation = operation;
        }
    }

    public class ActiveDirectoryService
    {
        private readonly Microsoft.Extensions.Logging.ILogger<ActiveDirectoryService> _logger;
        private readonly string _server;
        private readonly int _port;
        private readonly string _user;
        private readonly string _pass;
        private readonly string _baseDn;
        private readonly string _activeUsersOu;
        private readonly string _websiteUsersOu;
        private readonly string _expiredUsersOu;
        private readonly string _groupsOu;
        private readonly string _computersOu;
        private readonly List<string> _requiredLogonComputers;
        private readonly string _upnSuffix;

        public ActiveDirectoryService(Microsoft.Extensions.Logging.ILogger<ActiveDirectoryService> logger, IConfiguration config)
        {
            _logger = logger;
            _server = config["ActiveDirectory:Server"] ?? "";
            _port = config.GetValue<int?>("ActiveDirectory:Port") ?? 0;
            _user = config["ActiveDirectory:User"] ?? "";
            _pass = config["ActiveDirectory:Password"] ?? "";
            _baseDn = config["ActiveDirectory:BaseDn"] ?? "";
            _activeUsersOu = config["ActiveDirectory:ActiveUsersOu"] ?? "";
            _websiteUsersOu = config["ActiveDirectory:WebsiteUsersOu"] ?? "";
            _expiredUsersOu = config["ActiveDirectory:ExpiredUsersOu"] ?? "";
            _groupsOu = config["ActiveDirectory:GroupsOu"] ?? "";
            _computersOu = config["ActiveDirectory:ComputersOu"] ?? "";
            _requiredLogonComputers = ParseComputerList(config["ActiveDirectory:RequiredLogonComputers"]);
            _upnSuffix = config["ActiveDirectory:UpnSuffix"] ?? BuildDomainNameFromBaseDn(_baseDn);
        }

        public async Task<bool> IsOnlineAsync()
        {
            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(_server, _port);
                if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask)
                {
                    return tcpClient.Connected;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AD] Falha ao testar conexao LDAP em {Server}:{Port}.", _server, _port);
                return false;
            }
        }

        private LdapConnection GetConnection()
        {
            LdapConnection conn;
            if (_port == 636 || _port == 3269) 
            {
                var options = new LdapConnectionOptions()
                    .UseSsl()
                    .ConfigureRemoteCertificateValidationCallback((sender, cert, chain, errors) => {
                        // Aceita se nao houver erros ou apenas erros ignoráveis, mas NAO aceita incondicionalmente
                        return errors == System.Net.Security.SslPolicyErrors.None || errors == System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
                    });
                conn = new LdapConnection(options);
            }
            else
            {
                conn = new LdapConnection();
            }
            conn.Connect(_server, _port);
            conn.Bind(LdapConnection.LdapV3, _user, _pass);
            return conn;
        }

        public async Task<List<AdUserDto>> GetUsersAsync()
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            var users = new List<AdUserDto>();
            using var conn = GetConnection();
            
            // Search globally for users, we will filter by DN path to categorize them
            var searchResults = conn.Search(
                _baseDn,
                LdapConnection.ScopeSub,
                "(&(objectCategory=person)(objectClass=user))",
                new[] { "sAMAccountName", "displayName", "userAccountControl", "accountExpires", "memberOf", "userWorkstations", "distinguishedName" },
                false);

            while (searchResults.HasMore())
            {
                try
                {
                    var entry = searchResults.Next();
                    var user = new AdUserDto
                    {
                        Username = GetAttribute(entry, "sAMAccountName"),
                        FullName = GetAttribute(entry, "displayName")
                    };

                    string dn = entry.Dn;
                    if (IsExpiredUsersDn(dn)) user.OuPath = "USUARIOS_EXPIRADOS";
                    else if (IsActiveUsersDn(dn)) user.OuPath = "USUARIOS";
                    else if (IsWebsiteUsersDn(dn)) user.OuPath = "USUARIOS_WEBSITE";
                    else continue; // Ignora admin accounts em outras pastas

                    int uac = GetAttributeAsInt(entry, "userAccountControl");
                    user.IsActive = (uac & 2) == 0;

                    long expires = GetAttributeAsLong(entry, "accountExpires");
                    if (expires > 0 && expires != long.MaxValue)
                        user.ExpiresAt = AccountExpiresToDisplayDate(expires);

                    foreach (string groupDn in GetAttributeValues(entry, "memberOf"))
                    {
                        var parts = groupDn.Split(',');
                        if (parts.Length > 0 && parts[0].StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                            user.Groups.Add(parts[0].Substring(3));
                    }

                    var workstations = GetAttribute(entry, "userWorkstations");
                    if (!string.IsNullOrEmpty(workstations))
                    {
                        user.Computers = workstations.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        user.AllowAllComputers = false;
                    }
                    else
                    {
                        user.AllowAllComputers = true; // sem restrição = todos os computadores
                    }

                    users.Add(user);
                }
                catch (LdapReferralException ex)
                {
                    _logger.LogDebug(ex, "[AD] Referral LDAP ignorado durante listagem de usuarios.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AD] Ignorando usuario LDAP com atributos invalidos.");
                }
            }
            return users;
        }

        public async Task MoveUserOuAsync(string username, bool toExpired)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            if (string.IsNullOrEmpty(userDn)) throw new Exception("Usuário não encontrado");

            // Para mover, renomeamos a entrada mudando o Parent DN
            string cn = userDn.Split(',')[0]; // ex: CN=Natanael Eduardo
            string newParent = toExpired ? _expiredUsersOu : _activeUsersOu;

            if (!toExpired)
            {
                EnsureRequiredLogonComputers(conn, userDn);
            }

            conn.Rename(userDn, cn, newParent, true);
        }

        public async Task EnsureRequiredLogonComputersAsync(string username)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string userDn = GetActiveUserDnOrThrow(conn, username);

            EnsureRequiredLogonComputers(conn, userDn);
        }

        public async Task<bool> TryEnsureRequiredLogonComputersAsync(string username)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string userDn = GetActiveUserDnOrThrow(conn, username);

            try
            {
                EnsureRequiredLogonComputers(conn, userDn);
                return true;
            }
            catch (LdapException ex) when (ex.ResultCode == 50)
            {
                _logger.LogWarning(ex, "[AD] Sem permissao para atualizar computadores obrigatorios de {Username}.", username);
                return false;
            }
        }

        public async Task DisableAndArchiveUserAsync(string username)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            if (string.IsNullOrEmpty(userDn)) throw new Exception("Usuário AD não encontrado");

            var res = conn.Search(
                _baseDn,
                LdapConnection.ScopeSub,
                $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))",
                new[] { "userAccountControl" },
                false);

            int userAccountControl = 512;
            if (res.HasMore())
            {
                int currentUserAccountControl = GetAttributeAsInt(res.Next(), "userAccountControl");
                if (currentUserAccountControl > 0) userAccountControl = currentUserAccountControl;
            }
            int disabledUserAccountControl = userAccountControl | 2;
            conn.Modify(userDn, new[] {
                new LdapModification(LdapModification.Replace, new LdapAttribute("userAccountControl", disabledUserAccountControl.ToString()))
            });

            if (!IsExpiredUsersDn(userDn))
            {
                string cn = userDn.Split(',')[0];
                conn.Rename(userDn, cn, _expiredUsersOu, true);
            }
        }

        public async Task<List<AdGroupDto>> GetGroupsAsync()
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            var groups = new List<AdGroupDto>();
            using var conn = GetConnection();
            var searchResults = conn.Search(
                _groupsOu,
                LdapConnection.ScopeSub,
                "(objectCategory=group)",
                new[] { "cn", "description" },
                false);

            while (searchResults.HasMore())
            {
                try
                {
                    var entry = searchResults.Next();
                    groups.Add(new AdGroupDto
                    {
                        Name = GetAttribute(entry, "cn"),
                        Description = GetAttribute(entry, "description")
                    });
                }
                catch (LdapException ex)
                {
                    _logger.LogWarning(ex, "[AD] Falha ao ler um grupo durante a listagem.");
                }
            }
            return groups;
        }

        public async Task<List<AdComputerDto>> GetComputersAsync()
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            var computers = new List<AdComputerDto>();
            using var conn = GetConnection();
            var searchResults = conn.Search(
                _computersOu,
                LdapConnection.ScopeSub,
                "(objectCategory=computer)",
                new[] { "cn", "description", "operatingSystem", "userAccountControl", "memberOf" },
                false);

            while (searchResults.HasMore())
            {
                try
                {
                    var entry = searchResults.Next();
                    var computer = new AdComputerDto
                    {
                        Name = GetAttribute(entry, "cn"),
                        Description = GetAttribute(entry, "description"),
                        OperatingSystem = GetAttribute(entry, "operatingSystem"),
                        IsActive = (GetAttributeAsInt(entry, "userAccountControl") & 2) == 0
                    };
                    computer.Groups.AddRange(GetManagedGroupNames(entry));
                    computers.Add(computer);
                }
                catch (LdapException ex)
                {
                    _logger.LogWarning(ex, "[AD] Falha ao ler um computador durante a listagem.");
                }
            }
            return computers
                .OrderBy(c => string.IsNullOrWhiteSpace(c.Description) ? c.Name : c.Description, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task CreateGroupAsync(string name, string? description)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            (name, description) = ValidateGroup(name, description);

            using var conn = GetConnection();
            if (!string.IsNullOrEmpty(GetGroupDn(conn, name))) throw new Exception("Já existe um grupo com este nome.");
            var attributes = new LdapAttributeSet
            {
                new LdapAttribute("objectClass", "group"),
                new LdapAttribute("cn", name),
                new LdapAttribute("sAMAccountName", name),
                // GROUP_TYPE_ACCOUNT_GROUP | GROUP_TYPE_SECURITY_ENABLED
                new LdapAttribute("groupType", "-2147483646")
            };
            if (!string.IsNullOrWhiteSpace(description)) attributes.Add(new LdapAttribute("description", description));
            conn.Add(new LdapEntry($"CN={name},{_groupsOu}", attributes));
            _logger.LogInformation("[AD] Grupo global de segurança criado: {GroupName}.", name);
        }

        public async Task UpdateGroupAsync(string currentName, string name, string? description)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            currentName = (currentName ?? "").Trim();
            (name, description) = ValidateGroup(name, description);

            using var conn = GetConnection();
            string groupDn = GetGroupDn(conn, currentName) ?? throw new Exception("Grupo não encontrado no AD.");
            if (!name.Equals(currentName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(GetGroupDn(conn, name)))
                throw new Exception("Já existe um grupo com este nome.");

            var entry = ReadEntry(conn, groupDn, "description");
            var modifications = BuildOptionalAttributeModification("description", GetAttribute(entry, "description"), description);
            modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("sAMAccountName", name)));
            conn.Modify(groupDn, modifications.ToArray());

            if (!name.Equals(currentName, StringComparison.Ordinal))
                conn.Rename(groupDn, $"CN={name}", true);

            _logger.LogInformation("[AD] Grupo atualizado: {OldGroupName} -> {GroupName}.", currentName, name);
        }

        public async Task DeleteGroupAsync(string name)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string groupDn = GetGroupDn(conn, (name ?? "").Trim()) ?? throw new Exception("Grupo não encontrado no AD.");
            conn.Delete(groupDn);
            _logger.LogInformation("[AD] Grupo excluído: {GroupName}.", name);
        }

        public async Task CreateComputerAsync(string name, string? description, string? operatingSystem, bool isActive)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            (name, description, operatingSystem) = ValidateComputer(name, description, operatingSystem);

            using var conn = GetConnection();
            if (!string.IsNullOrEmpty(GetComputerDn(conn, name))) throw new Exception("Já existe um computador com este nome.");
            var attributes = new LdapAttributeSet
            {
                new LdapAttribute("objectClass", "computer"),
                new LdapAttribute("cn", name),
                new LdapAttribute("sAMAccountName", name + "$"),
                new LdapAttribute("userAccountControl", isActive ? "4096" : "4098")
            };
            if (!string.IsNullOrWhiteSpace(_upnSuffix)) attributes.Add(new LdapAttribute("dNSHostName", $"{name.ToLowerInvariant()}.{_upnSuffix}"));
            if (!string.IsNullOrWhiteSpace(description)) attributes.Add(new LdapAttribute("description", description));
            if (!string.IsNullOrWhiteSpace(operatingSystem)) attributes.Add(new LdapAttribute("operatingSystem", operatingSystem));
            conn.Add(new LdapEntry($"CN={name},{_computersOu}", attributes));
            _logger.LogInformation("[AD] Objeto de computador criado: {ComputerName}.", name);
        }

        public async Task UpdateComputerAsync(string currentName, string name, string? description, string? operatingSystem, bool isActive)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            currentName = (currentName ?? "").Trim().ToUpperInvariant();
            (name, description, operatingSystem) = ValidateComputer(name, description, operatingSystem);

            using var conn = GetConnection();
            string computerDn = GetComputerDn(conn, currentName) ?? throw new Exception("Computador não encontrado no AD.");
            if (!name.Equals(currentName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(GetComputerDn(conn, name)))
                throw new Exception("Já existe um computador com este nome.");

            var entry = ReadEntry(conn, computerDn, "description", "operatingSystem", "userAccountControl");
            int userAccountControl = GetAttributeAsInt(entry, "userAccountControl");
            if (userAccountControl == 0) userAccountControl = 4096;
            userAccountControl = isActive ? userAccountControl & ~2 : userAccountControl | 2;

            var modifications = BuildOptionalAttributeModification("description", GetAttribute(entry, "description"), description);
            modifications.AddRange(BuildOptionalAttributeModification("operatingSystem", GetAttribute(entry, "operatingSystem"), operatingSystem));
            modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("sAMAccountName", name + "$")));
            modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("userAccountControl", userAccountControl.ToString())));
            if (!string.IsNullOrWhiteSpace(_upnSuffix))
                modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("dNSHostName", $"{name.ToLowerInvariant()}.{_upnSuffix}")));
            conn.Modify(computerDn, modifications.ToArray());

            if (!name.Equals(currentName, StringComparison.Ordinal))
                conn.Rename(computerDn, $"CN={name}", true);

            _logger.LogInformation("[AD] Computador atualizado: {OldComputerName} -> {ComputerName}.", currentName, name);
        }

        public async Task DeleteComputerAsync(string name)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string computerDn = GetComputerDn(conn, (name ?? "").Trim()) ?? throw new Exception("Computador não encontrado no AD.");
            conn.Delete(computerDn);
            _logger.LogInformation("[AD] Computador excluído: {ComputerName}.", name);
        }

        public async Task CreateUserAsync(string username, string fullName, string password, string? email = null, string? phone = null, bool fromWebsite = false, bool forcePasswordChange = false, string? commonName = null)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            EnsureSecurePasswordTransport(password);
            using var conn = GetConnection();
            commonName = string.IsNullOrWhiteSpace(commonName) ? fullName : commonName.Trim();
            var attributes = new LdapAttributeSet
            {
                new LdapAttribute("objectClass", "user"),
                new LdapAttribute("sAMAccountName", username),
                new LdapAttribute("cn", commonName),
                new LdapAttribute("displayName", fullName)
            };

            string[] nameParts = fullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            attributes.Add(new LdapAttribute("givenName", nameParts.Length > 0 ? nameParts[0] : fullName));
            if (nameParts.Length > 1) {
                attributes.Add(new LdapAttribute("sn", nameParts[1]));
            }
            if (string.IsNullOrWhiteSpace(_upnSuffix))
                throw new Exception("Não foi possível determinar o domínio UPN do Active Directory.");

            attributes.Add(new LdapAttribute("userPrincipalName", $"{username}@{_upnSuffix}"));
            if (!string.IsNullOrWhiteSpace(email))
            {
                attributes.Add(new LdapAttribute("mail", email));
            }
            if (!string.IsNullOrWhiteSpace(phone))
            {
                string cleanPhone = System.Text.RegularExpressions.Regex.Replace(phone, @"\D", "");
                if (!string.IsNullOrWhiteSpace(cleanPhone))
                {
                    attributes.Add(new LdapAttribute("telephoneNumber", cleanPhone));
                }
            }
            var requiredWorkstations = BuildComputerList("", includeRequired: true);
            if (requiredWorkstations.Count > 0)
            {
                attributes.Add(new LdapAttribute("userWorkstations", string.Join(",", requiredWorkstations)));
            }

            string targetOu = fromWebsite && !string.IsNullOrEmpty(_websiteUsersOu) ? _websiteUsersOu : _activeUsersOu;
            var entry = new LdapEntry($"CN={commonName},{targetOu}", attributes);
            conn.Add(entry);

            if (!string.IsNullOrEmpty(password))
            {
                var encodedPass = System.Text.Encoding.Unicode.GetBytes($"\"{password}\"");
                var passMod = new LdapModification(LdapModification.Replace, new LdapAttribute("unicodePwd", encodedPass));
                var uacMod = new LdapModification(LdapModification.Replace, new LdapAttribute("userAccountControl", "66048")); // NORMAL_ACCOUNT | DONT_EXPIRE_PASSWORD
                var pwdLastSetMod = new LdapModification(LdapModification.Replace, new LdapAttribute("pwdLastSet", forcePasswordChange ? "0" : "-1"));
                try
                {
                    _logger.LogInformation("[AD] Iniciando definição de senha para usuário: {Username}.", username);
                    conn.Modify(entry.Dn, new[] { passMod });
                    conn.Modify(entry.Dn, new[] { uacMod, pwdLastSetMod });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AD] Falha ao definir senha inicial para {Username}. Excluindo a conta recém-criada para evitar inconsistência.", username);
                    try
                    {
                        conn.Delete(entry.Dn);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx, "[AD] Falha ao excluir a conta incompleta {Username}.", username);
                    }
                    throw BuildPasswordException(ex);
                }
            }
        }

        public async Task DeleteUserAsync(string username)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            if (!string.IsNullOrEmpty(userDn))
            {
                conn.Delete(userDn);
            }
        }

        public async Task<bool> UserExistsAsync(string username)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            return !string.IsNullOrEmpty(GetUserDn(conn, username));
        }

        public async Task ActivateAndRestoreUserAsync(string username)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string userDn = GetUserDn(conn, username) ?? throw new Exception("Usuário AD não encontrado");
            var entry = ReadEntry(conn, userDn, "userAccountControl");
            int userAccountControl = GetAttributeAsInt(entry, "userAccountControl");
            if (userAccountControl == 0) userAccountControl = 512;
            userAccountControl &= ~2;

            conn.Modify(userDn, new[]
            {
                new LdapModification(LdapModification.Replace, new LdapAttribute("userAccountControl", userAccountControl.ToString()))
            });
            EnsureRequiredLogonComputers(conn, userDn);

            if (!IsActiveUsersDn(userDn))
            {
                string rdn = userDn.Split(',')[0];
                conn.Rename(userDn, rdn, _activeUsersOu, true);
            }
        }

        public async Task SetUserPasswordAsync(string username, string password, bool forceChangeOnNextLogon)
        {
            if (string.IsNullOrWhiteSpace(password)) throw new Exception("Senha obrigatória");
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            EnsureSecurePasswordTransport(password);
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            if (string.IsNullOrEmpty(userDn)) throw new Exception("Usuário AD não encontrado");

            var encodedPass = System.Text.Encoding.Unicode.GetBytes($"\"{password}\"");
            var mods = new List<LdapModification>
            {
                new LdapModification(LdapModification.Replace, new LdapAttribute("unicodePwd", encodedPass))
            };

            if (forceChangeOnNextLogon)
            {
                mods.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("pwdLastSet", "0")));
            }

            conn.Modify(userDn, mods.ToArray());
        }

        public async Task UpdateTelephoneAsync(string username, string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return;
            if (!await IsOnlineAsync()) return; // não crítico, não lança exceção
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            if (string.IsNullOrEmpty(userDn)) return;

            // Limpa tudo que não for dígito: (34) 9 9176-5784 → 34991765784
            string cleanPhone = System.Text.RegularExpressions.Regex.Replace(phone, @"\D", "");
            if (string.IsNullOrWhiteSpace(cleanPhone)) return;

            try
            {
                conn.Modify(userDn, new[]
                {
                    new LdapModification(LdapModification.Replace, new LdapAttribute("telephoneNumber", cleanPhone))
                });
                _logger.LogInformation("[AD] telephoneNumber atualizado para {Username}: {Phone}.", username, cleanPhone);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AD] Falha ao atualizar telephoneNumber de {Username}.", username);
            }
        }

        public async Task UpdateUserDetailsAsync(string username, string fullName, string? email, string? whatsapp, string? password, bool isActive, bool passwordNeverExpires)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string dn = GetUserDn(conn, username) ?? throw new Exception("Usuario nao encontrado no AD.");

            var modifications = new List<LdapModification>();

            if (!string.IsNullOrWhiteSpace(fullName))
            {
                modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("displayName", fullName)));
                string[] nameParts = fullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("givenName", nameParts.Length > 0 ? nameParts[0] : fullName)));
                if (nameParts.Length > 1)
                {
                    modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("sn", nameParts[1])));
                }
            }

            if (whatsapp != null)
            {
                string cleanPhone = System.Text.RegularExpressions.Regex.Replace(whatsapp, @"\D", "");
                if (string.IsNullOrWhiteSpace(cleanPhone))
                {
                    modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("telephoneNumber", "")));
                }
                else
                {
                    modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("telephoneNumber", cleanPhone)));
                }
            }

            if (email != null)
            {
                modifications.Add(new LdapModification(
                    LdapModification.Replace,
                    new LdapAttribute("mail", email)));
            }

            var res = conn.Search(_baseDn, LdapConnection.ScopeSub,
                $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))",
                new[] { "userAccountControl" }, false);
            
            if (res.HasMore())
            {
                var entry = res.Next();
                long currentUac = GetAttributeAsLong(entry, "userAccountControl");
                if (currentUac == 0) currentUac = 512; 

                currentUac &= ~2L; 
                currentUac &= ~65536L; 

                if (!isActive) currentUac |= 2L;
                if (passwordNeverExpires) currentUac |= 65536L;

                modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("userAccountControl", currentUac.ToString())));
            }

            if (!string.IsNullOrEmpty(password))
            {
                EnsureSecurePasswordTransport(password);
                var encodedPass = System.Text.Encoding.Unicode.GetBytes($"\"{password}\"");
                modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("unicodePwd", encodedPass)));
                modifications.Add(new LdapModification(LdapModification.Replace, new LdapAttribute("pwdLastSet", "-1")));
            }

            if (modifications.Count > 0)
            {
                conn.Modify(dn, modifications.ToArray());
            }

            if (!string.IsNullOrWhiteSpace(fullName))
            {
                string newRdn = $"CN={fullName}";
                if (!dn.StartsWith(newRdn, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        conn.Rename(dn, newRdn, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AD] Falha ao renomear o CN para {NewRdn}.", newRdn);
                    }
                }
            }
        }

        public async Task<AdUserDto?> GetUserDetailsAsync(string username)
        {
            if (!await IsOnlineAsync()) return null;
            using var conn = GetConnection();
            var res = conn.Search(_baseDn, LdapConnection.ScopeSub,
                $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))",
                new[] { "sAMAccountName", "displayName", "userAccountControl", "accountExpires", "memberOf", "userWorkstations", "mail", "telephoneNumber" }, false);
            if (!res.HasMore()) return null;
            var entry = res.Next();

            var workstations = GetAttribute(entry, "userWorkstations");
            long expires = GetAttributeAsLong(entry, "accountExpires");
            long uac = GetAttributeAsLong(entry, "userAccountControl");

            var dto = new AdUserDto
            {
                Username = GetAttribute(entry, "sAMAccountName"),
                FullName = GetAttribute(entry, "displayName"),
                AllowAllComputers = string.IsNullOrEmpty(workstations),
                Computers = string.IsNullOrEmpty(workstations)
                    ? new List<string>()
                    : workstations.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                ExpiresAt = (expires > 0 && expires != long.MaxValue)
                    ? AccountExpiresToDisplayDate(expires)
                    : null,
                IsActive = (uac & 2) == 0,
                UserAccountControl = uac > 0 ? uac : null,
                Email = GetAttribute(entry, "mail"),
                TelephoneNumber = GetAttribute(entry, "telephoneNumber")
            };

            foreach (string groupDn in GetAttributeValues(entry, "memberOf"))
            {
                var parts = groupDn.Split(',');
                if (parts.Length > 0 && parts[0].StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    dto.Groups.Add(parts[0].Substring(3));
            }

            return dto;
        }

        public async Task DuplicateUserAsync(string sourceUsername, string newUsername, string newFullName, string password, string? whatsapp = null)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");

            // 1. Obter dados do usuário fonte
            var source = await GetUserDetailsAsync(sourceUsername)
                ?? throw new Exception($"Usuário fonte '{sourceUsername}' não encontrado.");

            // 2. Criar novo usuário (com os computadores obrigatórios)
            await CreateUserAsync(newUsername, newFullName, password, null, whatsapp);

            using var conn = GetConnection();
            string? newUserDn = GetUserDn(conn, newUsername)
                ?? throw new Exception("Falha ao localizar o usuário recém-criado.");

            var mods = new List<LdapModification>();

            // 3. Copiar computadores do fonte (incluindo obrigatórios)
            if (!source.AllowAllComputers && source.Computers.Count > 0)
            {
                var computers = BuildComputerList(string.Join(",", source.Computers), includeRequired: true);
                mods.Add(new LdapModification(LdapModification.Replace,
                    new LdapAttribute("userWorkstations", string.Join(",", computers))));
            }

            // 4. Copiar vencimento
            if (source.ExpiresAt.HasValue)
            {
                mods.Add(new LdapModification(LdapModification.Replace,
                    new LdapAttribute("accountExpires", DisplayDateToAccountExpires(source.ExpiresAt.Value).ToString())));
            }

            // 4.1 Copiar opções da conta (UserAccountControl)
            if (source.UserAccountControl.HasValue)
            {
                mods.Add(new LdapModification(LdapModification.Replace,
                    new LdapAttribute("userAccountControl", source.UserAccountControl.Value.ToString())));
            }

            if (mods.Count > 0)
                conn.Modify(newUserDn, mods.ToArray());

            // 5. Copiar grupos (exceto Domain Users que já é automático)
            foreach (var groupName in source.Groups)
            {
                if (string.Equals(groupName, "Domain Users", StringComparison.OrdinalIgnoreCase)) continue;
                string? groupDn = GetGroupDn(conn, groupName);
                if (!string.IsNullOrEmpty(groupDn))
                {
                    try
                    {
                        conn.Modify(groupDn, new[] { new LdapModification(LdapModification.Add, new LdapAttribute("member", newUserDn)) });
                    }
                    catch (LdapException ex) when (ex.ResultCode == 20)
                    {
                        _logger.LogInformation("[AD] Usuario duplicado {Username} ja pertence ao grupo {GroupName}.", newUsername, groupName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AD] Falha ao copiar o grupo {GroupName} para o usuario duplicado {Username}.", groupName, newUsername);
                    }
                }
            }

            _logger.LogInformation("[AD] Usuário {New} duplicado de {Source}.", newUsername, sourceUsername);
        }

        public async Task SetUserExpirationAsync(string username, DateTime? expiresAt)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            if (string.IsNullOrEmpty(userDn)) return;

            long expiresValue = 0; // Never expires
            if (expiresAt.HasValue)
            {
                expiresValue = DisplayDateToAccountExpires(expiresAt.Value);
            }

            var mod = new LdapModification(LdapModification.Replace, new LdapAttribute("accountExpires", expiresValue.ToString()));
            conn.Modify(userDn, new[] { mod });
        }

        public async Task ManageUserGroupAsync(string username, string groupName, bool add)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            string? userDn = GetUserDn(conn, username);
            string? groupDn = GetGroupDn(conn, groupName);

            if (string.IsNullOrEmpty(userDn) || string.IsNullOrEmpty(groupDn)) return;

            var op = add ? LdapModification.Add : LdapModification.Delete;
            var mod = new LdapModification(op, new LdapAttribute("member", userDn));
            try
            {
                conn.Modify(groupDn, new[] { mod });
            }
            catch (LdapException ex) when ((add && ex.ResultCode == 20) || (!add && ex.ResultCode == 16))
            {
                _logger.LogInformation(
                    "[AD] Associacao entre {Username} e {GroupName} ja estava no estado solicitado. Adicionar: {Add}.",
                    username, groupName, add);
            }
        }

        public async Task SetComputerGroupsAsync(string computerName, IEnumerable<string>? groupNames)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");

            var desiredNames = (groupNames ?? Array.Empty<string>())
                .Select(name => (name ?? "").Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (desiredNames.Count > 100) throw new Exception("Selecione no máximo 100 grupos por computador.");

            using var conn = GetConnection();
            string computerDn = GetComputerDn(conn, computerName)
                ?? throw new Exception($"Computador '{computerName}' não encontrado no AD.");
            var computerEntry = ReadEntry(conn, computerDn, "memberOf");
            var currentGroups = GetAttributeValues(computerEntry, "memberOf")
                .Where(IsManagedGroupDn)
                .Select(dn => new { Name = GetCnFromDn(dn), Dn = dn })
                .Where(group => !string.IsNullOrWhiteSpace(group.Name))
                .GroupBy(group => group.Name!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(group => group.Name!, group => group.Dn, StringComparer.OrdinalIgnoreCase);

            var desiredGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string groupName in desiredNames)
            {
                string groupDn = GetGroupDn(conn, groupName)
                    ?? throw new Exception($"Grupo '{groupName}' não encontrado no AD.");
                desiredGroups[groupName] = groupDn;
            }

            foreach (var group in desiredGroups.Where(group => !currentGroups.ContainsKey(group.Key)))
                AddComputerToGroup(conn, computerDn, computerName, group.Key, group.Value);

            foreach (var group in currentGroups.Where(group => !desiredGroups.ContainsKey(group.Key)))
            {
                try
                {
                    conn.Modify(group.Value, new[]
                    {
                        new LdapModification(LdapModification.Delete, new LdapAttribute("member", computerDn))
                    });
                    _logger.LogInformation("[AD] Computador {Computer} removido do grupo {GroupName}.", computerName, group.Key);
                }
                catch (LdapException ex) when (ex.ResultCode == 16)
                {
                    _logger.LogInformation("[AD] Computador {Computer} ja nao pertencia ao grupo {GroupName}.", computerName, group.Key);
                }
            }
        }

        public async Task SetUserComputersAsync(
            string username,
            string computersStr,
            bool allowAllComputers = false,
            IReadOnlyDictionary<string, string>? computerGroups = null)
        {
            if (!await IsOnlineAsync()) throw new Exception("Servidor AD offline");
            using var conn = GetConnection();
            
            // Pega computadores atuais (antes de atualizar) para saber quais remover
            var res = conn.Search(_baseDn, LdapConnection.ScopeSub, $"(&(objectClass=user)(sAMAccountName={username}))", new[] { "distinguishedName", "userWorkstations", "memberOf" }, false);
            if (!res.HasMore()) throw new Exception($"Usuário AD '{username}' não encontrado.");
            var entry = res.Next();
            string userDn = entry.Dn;
            
            string oldWorkstationsStr = GetAttribute(entry, "userWorkstations");
            var oldComps = ParseComputerList(oldWorkstationsStr);
            
            // Se allowAllComputers for true, não incluímos requeridos, e listamos string vazia (que no AD significa liberar todos)
            var newComps = allowAllComputers ? new List<string>() : BuildComputerList(computersStr, includeRequired: true);

            var currentGroupDns = new HashSet<string>(
                GetAttributeValues(entry, "memberOf"),
                StringComparer.OrdinalIgnoreCase);
            var groupsToAdd = new List<(string Computer, string GroupName, string GroupDn)>();
            var groupsToRemove = new List<(string Computer, string GroupName, string GroupDn)>();

            // O grupo de acesso e derivado da descricao do computador:
            // descricao SRV01_01 -> grupo ACESSO_SRV01-01.
            foreach (var pc in newComps)
            {
                if (IsRequiredLogonComputer(pc)) continue;

                var resolvedGroup = ResolveComputerGroup(conn, pc, computerGroups, "add");
                if (!currentGroupDns.Contains(resolvedGroup.GroupDn))
                    groupsToAdd.Add((pc, resolvedGroup.GroupName, resolvedGroup.GroupDn));
            }

            foreach (var pc in oldComps)
            {
                if (IsRequiredLogonComputer(pc)) continue;

                if (!newComps.Contains(pc))
                {
                    var resolvedGroup = ResolveComputerGroup(conn, pc, computerGroups, "remove");
                    if (currentGroupDns.Contains(resolvedGroup.GroupDn))
                        groupsToRemove.Add((pc, resolvedGroup.GroupName, resolvedGroup.GroupDn));
                }
            }

            foreach (var item in groupsToAdd)
            {
                conn.Modify(item.GroupDn, new[] { new LdapModification(LdapModification.Add, new LdapAttribute("member", userDn)) });
                currentGroupDns.Add(item.GroupDn);
                _logger.LogInformation("[AD] Usuário {Username} adicionado ao grupo {GroupName} pelo computador {Computer}.",
                    username, item.GroupName, item.Computer);
            }

            foreach (var item in groupsToRemove)
            {
                conn.Modify(item.GroupDn, new[] { new LdapModification(LdapModification.Delete, new LdapAttribute("member", userDn)) });
                currentGroupDns.Remove(item.GroupDn);
                _logger.LogInformation("[AD] Usuário {Username} removido do grupo {GroupName} pelo computador {Computer}.",
                    username, item.GroupName, item.Computer);
            }

            var attrValue = string.Join(",", newComps);
            LdapModification? workstationMod = null;
            if (string.IsNullOrEmpty(attrValue))
            {
                if (!string.IsNullOrEmpty(oldWorkstationsStr))
                    workstationMod = new LdapModification(LdapModification.Delete, new LdapAttribute("userWorkstations"));
            }
            else
            {
                workstationMod = new LdapModification(LdapModification.Replace, new LdapAttribute("userWorkstations", attrValue));
            }

            if (workstationMod != null)
                conn.Modify(userDn, new[] { workstationMod });

            _logger.LogInformation("[AD] Computadores de {Username} sincronizados: {Computers}. Liberar todos: {AllowAll}.",
                username, string.IsNullOrEmpty(attrValue) ? "(nenhum)" : attrValue, allowAllComputers);
        }

        private void EnsureSecurePasswordTransport(string? password)
        {
            if (string.IsNullOrEmpty(password)) return;
            if (_port != 636 && _port != 3269)
            {
                throw new Exception(
                    "O Active Directory exige conexão LDAPS para definir senhas. " +
                    "Configure um certificado no controlador de domínio e use ActiveDirectory__Port=636.");
            }
        }

        private static Exception BuildPasswordException(Exception inner)
        {
            if (inner is LdapException ldapEx && ldapEx.ResultCode == LdapException.UnwillingToPerform)
            {
                return new Exception(
                    "O Active Directory recusou a definição da senha. Confirme que a conexão usa LDAPS e que a senha atende à política do domínio.",
                    inner);
            }

            return new Exception("Não foi possível definir a senha inicial no Active Directory.", inner);
        }

        private static DateTime AccountExpiresToDisplayDate(long accountExpires)
        {
            var localExpiration = DateTimeOffset.FromFileTime(accountExpires).ToLocalTime();
            return localExpiration.Date.AddDays(-1);
        }

        private static long DisplayDateToAccountExpires(DateTime displayDate)
        {
            var endBoundary = new DateTimeOffset(
                displayDate.Date.AddDays(1),
                TimeZoneInfo.Local.GetUtcOffset(displayDate.Date.AddDays(1)));
            return endBoundary.ToFileTime();
        }

        private string? GetComputerAccessGroupName(LdapConnection conn, string computerName)
        {
            var results = conn.Search(
                _computersOu,
                LdapConnection.ScopeSub,
                $"(&(objectCategory=computer)(cn={EscapeLdapFilterValue(computerName)}))",
                new[] { "description", "memberOf" },
                false);

            if (!results.HasMore()) return null;

            var computer = results.Next();
            string description = GetAttribute(computer, "description");
            string? describedGroup = GetConventionComputerGroupName(description);

            if (!string.IsNullOrWhiteSpace(describedGroup) && !string.IsNullOrWhiteSpace(GetGroupDn(conn, describedGroup)))
                return describedGroup;

            string? membershipGroup = GetManagedGroupNames(computer)
                .OrderByDescending(name => name.StartsWith("ACESSO_", StringComparison.OrdinalIgnoreCase))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return membershipGroup ?? describedGroup;
        }

        private static string? GetConventionComputerGroupName(string? description)
        {
            var match = Regex.Match(
                description ?? "",
                @"^\s*SRV(?<server>\d{2})_(?<computer>\d{2})\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success
                ? $"ACESSO_SRV{match.Groups["server"].Value}-{match.Groups["computer"].Value}".ToUpperInvariant()
                : null;
        }

        private (string GroupName, string GroupDn) ResolveComputerGroup(
            LdapConnection conn,
            string computerName,
            IReadOnlyDictionary<string, string>? computerGroups,
            string operation)
        {
            string? suggestedGroup = GetComputerAccessGroupName(conn, computerName);
            string? groupName = suggestedGroup;
            string? groupDn = string.IsNullOrWhiteSpace(groupName) ? null : GetGroupDn(conn, groupName);
            bool manuallySelected = false;

            if (string.IsNullOrEmpty(groupDn)
                && computerGroups != null
                && computerGroups.TryGetValue(computerName, out var selectedGroup)
                && !string.IsNullOrWhiteSpace(selectedGroup))
            {
                groupName = selectedGroup.Trim();
                groupDn = GetGroupDn(conn, groupName);
                if (string.IsNullOrEmpty(groupDn))
                    throw new Exception($"O grupo selecionado '{groupName}' não foi encontrado no AD.");
                manuallySelected = true;
            }

            if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(groupDn))
            {
                throw new ComputerGroupSelectionRequiredException(computerName, suggestedGroup, operation);
            }

            if (manuallySelected)
            {
                string computerDn = GetComputerDn(conn, computerName)
                    ?? throw new Exception($"Computador '{computerName}' não encontrado no AD.");
                AddComputerToGroup(conn, computerDn, computerName, groupName, groupDn);
            }

            return (groupName, groupDn);
        }

        private void AddComputerToGroup(LdapConnection conn, string computerDn, string computerName, string groupName, string groupDn)
        {
            try
            {
                conn.Modify(groupDn, new[]
                {
                    new LdapModification(LdapModification.Add, new LdapAttribute("member", computerDn))
                });
                _logger.LogInformation("[AD] Computador {Computer} adicionado ao grupo {GroupName}.", computerName, groupName);
            }
            catch (LdapException ex) when (ex.ResultCode == 20)
            {
                _logger.LogInformation("[AD] Computador {Computer} ja pertence ao grupo {GroupName}.", computerName, groupName);
            }
        }

        private List<string> GetManagedGroupNames(LdapEntry entry)
        {
            return GetAttributeValues(entry, "memberOf")
                .Where(IsManagedGroupDn)
                .Select(GetCnFromDn)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsManagedGroupDn(string groupDn)
        {
            return groupDn.Equals(_groupsOu, StringComparison.OrdinalIgnoreCase)
                || groupDn.EndsWith("," + _groupsOu, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetCnFromDn(string distinguishedName)
        {
            string firstPart = distinguishedName.Split(',', 2)[0].Trim();
            return firstPart.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)
                ? firstPart[3..]
                : null;
        }

        private static string EscapeLdapFilterValue(string value)
        {
            return value
                .Replace(@"\", @"\5c")
                .Replace("*", @"\2a")
                .Replace("(", @"\28")
                .Replace(")", @"\29")
                .Replace("\0", @"\00");
        }

        private void EnsureRequiredLogonComputers(LdapConnection conn, string userDn)
        {
            if (_requiredLogonComputers.Count == 0) return;

            var res = conn.Search(userDn, LdapConnection.ScopeBase, "(objectClass=*)", new[] { "userWorkstations" }, false);
            if (!res.HasMore()) return;

            var entry = res.Next();
            var computers = BuildComputerList(GetAttribute(entry, "userWorkstations"), includeRequired: true);
            var mod = new LdapModification(LdapModification.Replace, new LdapAttribute("userWorkstations", string.Join(",", computers)));
            conn.Modify(userDn, new[] { mod });
        }

        private bool IsRequiredLogonComputer(string computerName)
        {
            return _requiredLogonComputers.Contains(computerName, StringComparer.OrdinalIgnoreCase);
        }

        private bool IsActiveUsersDn(string userDn)
        {
            return userDn.EndsWith("," + _activeUsersOu, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsWebsiteUsersDn(string userDn)
        {
            if (string.IsNullOrEmpty(_websiteUsersOu)) return false;
            return userDn.EndsWith("," + _websiteUsersOu, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsExpiredUsersDn(string userDn)
        {
            return userDn.EndsWith("," + _expiredUsersOu, StringComparison.OrdinalIgnoreCase);
        }

        private List<string> BuildComputerList(string? computersStr, bool includeRequired)
        {
            var computers = ParseComputerList(computersStr);
            if (!includeRequired) return computers;

            foreach (var requiredComputer in _requiredLogonComputers)
            {
                if (!computers.Contains(requiredComputer, StringComparer.OrdinalIgnoreCase))
                {
                    computers.Add(requiredComputer);
                }
            }

            return computers;
        }

        private static List<string> ParseComputerList(string? computersStr)
        {
            if (string.IsNullOrWhiteSpace(computersStr)) return new List<string>();

            return computersStr
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildDomainNameFromBaseDn(string baseDn)
        {
            return string.Join(".",
                baseDn.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    .Select(part => part.Substring(3))
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private string? GetUserDn(LdapConnection conn, string username)
        {
            var res = conn.Search(_baseDn, LdapConnection.ScopeSub, $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))", new[] { "distinguishedName" }, false);
            while (res.HasMore())
            {
                try
                {
                    return res.Next().Dn;
                }
                catch (LdapReferralException ex)
                {
                    _logger.LogDebug(ex, "[AD] Referral LDAP ignorado ao procurar usuario {Username}.", username);
                }
            }
            return null;
        }

        private string GetActiveUserDnOrThrow(LdapConnection conn, string username)
        {
            string? userDn = GetUserDn(conn, username);
            if (string.IsNullOrEmpty(userDn)) throw new Exception("Usuário AD não encontrado");
            if (!IsActiveUsersDn(userDn)) throw new Exception("Usuário AD precisa estar na pasta USUARIOS.");
            return userDn;
        }

        private string? GetGroupDn(LdapConnection conn, string groupName)
        {
            var res = conn.Search(_groupsOu, LdapConnection.ScopeSub, $"(&(objectCategory=group)(cn={EscapeLdapFilterValue(groupName)}))", new[] { "distinguishedName" }, false);
            if (res.HasMore()) return res.Next().Dn;
            return null;
        }

        private string? GetComputerDn(LdapConnection conn, string computerName)
        {
            var res = conn.Search(
                _computersOu,
                LdapConnection.ScopeSub,
                $"(&(objectCategory=computer)(cn={EscapeLdapFilterValue(computerName)}))",
                new[] { "distinguishedName" },
                false);
            if (res.HasMore()) return res.Next().Dn;
            return null;
        }

        private static (string Name, string Description) ValidateGroup(string? name, string? description)
        {
            name = (name ?? "").Trim();
            if (!Regex.IsMatch(name, @"^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,63}$"))
                throw new Exception("O nome do grupo deve ter de 1 a 64 caracteres e usar apenas letras, números, espaço, ponto, hífen ou sublinhado.");
            if (name.Length > 20)
                throw new Exception("O nome do grupo deve ter no máximo 20 caracteres para compatibilidade com sAMAccountName.");

            description = (description ?? "").Trim();
            if (description.Length > 256) throw new Exception("A descrição deve ter no máximo 256 caracteres.");
            return (name, description);
        }

        private static (string Name, string Description, string OperatingSystem) ValidateComputer(string? name, string? description, string? operatingSystem)
        {
            name = (name ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(name, @"^(?!\d+$)[A-Z0-9][A-Z0-9-]{0,14}$"))
                throw new Exception("O nome do computador deve ter de 1 a 15 caracteres, usar letras, números ou hífen e não pode conter apenas números.");

            description = (description ?? "").Trim();
            operatingSystem = (operatingSystem ?? "").Trim();
            if (description.Length > 256) throw new Exception("A descrição deve ter no máximo 256 caracteres.");
            if (operatingSystem.Length > 128) throw new Exception("O sistema operacional deve ter no máximo 128 caracteres.");
            return (name, description, operatingSystem);
        }

        private static LdapEntry ReadEntry(LdapConnection conn, string dn, params string[] attributes)
        {
            var result = conn.Search(dn, LdapConnection.ScopeBase, "(objectClass=*)", attributes, false);
            if (!result.HasMore()) throw new Exception("Objeto não encontrado no AD.");
            return result.Next();
        }

        private static List<LdapModification> BuildOptionalAttributeModification(string attributeName, string currentValue, string newValue)
        {
            if (string.IsNullOrWhiteSpace(newValue))
            {
                return string.IsNullOrWhiteSpace(currentValue)
                    ? new List<LdapModification>()
                    : new List<LdapModification> { new(LdapModification.Delete, new LdapAttribute(attributeName)) };
            }

            return new List<LdapModification>
            {
                new(LdapModification.Replace, new LdapAttribute(attributeName, newValue))
            };
        }

        private string GetAttribute(LdapEntry entry, string attrName)
        {
            try
            {
                var attr = entry.GetAttribute(attrName);
                return attr?.StringValue ?? "";
            }
            catch { return ""; }
        }

        private IEnumerable<string> GetAttributeValues(LdapEntry entry, string attrName)
        {
            try
            {
                var attr = entry.GetAttribute(attrName);
                return attr?.StringValueArray ?? Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        private int GetAttributeAsInt(LdapEntry entry, string attrName)
        {
            if (int.TryParse(GetAttribute(entry, attrName), out int val)) return val;
            return 0;
        }

        private long GetAttributeAsLong(LdapEntry entry, string attrName)
        {
            if (long.TryParse(GetAttribute(entry, attrName), out long val)) return val;
            return 0;
        }
    }
}
