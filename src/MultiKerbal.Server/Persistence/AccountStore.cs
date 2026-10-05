using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiKerbal.Common;

namespace MultiKerbal.Server.Persistence;

/// <summary>Cuenta de un jugador: su nombre es suyo mientras sepa la contraseña.</summary>
internal sealed class Account
{
    public string Name { get; set; } = string.Empty;

    /// <summary>PBKDF2-SHA256 de la contraseña con <see cref="Salt"/> (nunca se guarda la contraseña).</summary>
    public string Hash { get; set; } = string.Empty;

    public string Salt { get; set; } = string.Empty;

    public int Iterations { get; set; }

    public DateTime Created { get; set; }

    public DateTime LastLogin { get; set; }
}

internal enum AccountCheck
{
    /// <summary>Contraseña correcta.</summary>
    Valid,

    /// <summary>Nombre libre: se registrará al entrar.</summary>
    NotRegistered,

    WrongPassword,
}

/// <summary>
/// Cuentas de los jugadores en Universe/accounts.json, con escritura atómica. Las contraseñas se guardan como
/// hash con sal (PBKDF2), así que ni quien administra el servidor puede leerlas: si alguien la olvida, se borra
/// su cuenta con el comando <c>account delete</c> y la vuelve a crear al entrar.
/// </summary>
internal sealed class AccountStore
{
    internal const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public AccountStore(string universeDirectory)
    {
        _path = Path.Combine(universeDirectory, "accounts.json");
    }

    public int Count => _accounts.Count;

    public IEnumerable<Account> All => _accounts.Values;

    public void Load()
    {
        _accounts.Clear();
        if (!File.Exists(_path))
            return;

        foreach (Account account in JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_path), JsonOptions) ?? [])
        {
            if (!string.IsNullOrWhiteSpace(account.Name))
                _accounts[account.Name] = account;
        }
    }

    public static bool IsValidNewPassword(string? password) =>
        password != null
        && password.Length >= ProtocolInfo.MinAccountPasswordLength
        && password.Length <= ProtocolInfo.MaxAccountPasswordLength;

    public AccountCheck Check(string name, string? password)
    {
        if (!_accounts.TryGetValue(name, out Account? account))
            return AccountCheck.NotRegistered;

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(account.Hash);
            salt = Convert.FromBase64String(account.Salt);
        }
        catch (FormatException)
        {
            return AccountCheck.WrongPassword;
        }

        byte[] actual = Derive(password ?? string.Empty, salt, account.Iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) ? AccountCheck.Valid : AccountCheck.WrongPassword;
    }

    public void Register(string name, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        DateTime now = DateTime.UtcNow;
        _accounts[name] = new Account
        {
            Name = name,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(password, salt, Iterations, HashBytes)),
            Iterations = Iterations,
            Created = now,
            LastLogin = now,
        };
        Save();
    }

    public void TouchLogin(string name)
    {
        if (_accounts.TryGetValue(name, out Account? account))
        {
            account.LastLogin = DateTime.UtcNow;
            Save();
        }
    }

    public bool Delete(string name)
    {
        if (!_accounts.Remove(name))
            return false;

        Save();
        return true;
    }

    /// <summary>El nombre tal como se registró (mayúsculas incluidas), o null si no hay cuenta.</summary>
    public string? RegisteredName(string name) => _accounts.TryGetValue(name, out Account? account) ? account.Name : null;

    private static byte[] Derive(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Math.Max(1, iterations), HashAlgorithmName.SHA256, length);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_accounts.Values.OrderBy(a => a.Name).ToList(), JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
