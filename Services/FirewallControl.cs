using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KdrEnet.Services;

/// <summary>
/// Opens Windows Firewall the way a remote ENET session does: Domain, Private,
/// and Public profiles go off, plus an allow-inbound rule. Stop restores the
/// exact on/off state from before the session.
/// </summary>
public static class FirewallControl
{
    public const string RuleName = "KDR ENET";
    private const string RuleDescription = "Allow inbound traffic for remote";

    private static readonly Profile[] Profiles =
    {
        new(1, "Domain", "domainprofile"),
        new(2, "Private", "privateprofile"),
        new(4, "Public", "publicprofile")
    };

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KDR Coding", "KdrEnet");

    private static string StatePath => Path.Combine(Folder, "firewall-restore.json");

    public static bool HasSavedState() => File.Exists(StatePath);

    public static bool AllowRuleExists()
    {
        object? policy = null;
        object? rules = null;
        try
        {
            policy = CreatePolicy();
            rules = Get(policy, "Rules");
            Get(rules!, "Item", RuleName);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(rules);
            Release(policy);
        }
    }

    public static string Open()
    {
        var prior = HasSavedState() ? Load() : ReadCurrent();
        if (!HasSavedState())
            Save(prior);

        try
        {
            foreach (var profile in Profiles)
                SetProfile(profile, enabled: false);
            AddAllowRule();
        }
        catch
        {
            try { Apply(prior); } catch { /* the original error is the one to show */ }
            DeleteState();
            throw;
        }

        return Describe(ReadCurrent());
    }

    public static void CloseSession()
    {
        RemoveAllowRule();
        if (!HasSavedState())
            return;

        var prior = Load();
        Apply(prior);
        var now = ReadCurrent();
        if (now.Domain != prior.Domain || now.Private != prior.Private || now.Public != prior.Public)
            throw new InvalidOperationException("Windows Firewall did not return to the settings from before the session.");

        DeleteState();
    }

    private static void Apply(FirewallSnapshot snapshot)
    {
        SetProfile(Profiles[0], snapshot.Domain);
        SetProfile(Profiles[1], snapshot.Private);
        SetProfile(Profiles[2], snapshot.Public);
    }

    private static FirewallSnapshot ReadCurrent()
    {
        object? policy = null;
        try
        {
            policy = CreatePolicy();
            return new FirewallSnapshot(Read(policy, 1), Read(policy, 2), Read(policy, 4));
        }
        finally
        {
            Release(policy);
        }
    }

    private static bool Read(object policy, int profile)
    {
        var value = policy.GetType().InvokeMember(
            "FirewallEnabled",
            BindingFlags.GetProperty,
            null,
            policy,
            new object[] { profile });
        return value is true;
    }

    private static void SetProfile(Profile profile, bool enabled)
    {
        object? policy = null;
        try
        {
            policy = CreatePolicy();
            try
            {
                policy.GetType().InvokeMember(
                    "FirewallEnabled",
                    BindingFlags.SetProperty,
                    null,
                    policy,
                    new object[] { profile.Id, enabled });
            }
            catch
            {
                // Read-back decides whether netsh has to do it.
            }
        }
        finally
        {
            Release(policy);
        }

        if (Is(profile.Id, enabled))
            return;

        Run("netsh", "advfirewall", "set", profile.Netsh, "state", enabled ? "on" : "off");
        if (!Is(profile.Id, enabled))
            throw new InvalidOperationException("Windows did not change the " + profile.Label + " firewall profile.");
    }

    private static bool Is(int profile, bool enabled)
    {
        object? policy = null;
        try
        {
            policy = CreatePolicy();
            return Read(policy, profile) == enabled;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(policy);
        }
    }

    private static void AddAllowRule()
    {
        RemoveAllowRule();
        object? policy = null;
        object? rules = null;
        object? rule = null;
        try
        {
            policy = CreatePolicy();
            rules = Get(policy, "Rules");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
                ?? throw new InvalidOperationException("Windows Firewall rules are unavailable.");
            rule = Activator.CreateInstance(ruleType)
                ?? throw new InvalidOperationException("Windows Firewall could not create a rule.");
            Set(rule, "Name", RuleName);
            Set(rule, "Description", RuleDescription);
            Set(rule, "Enabled", true);
            Set(rule, "Direction", 1);
            Set(rule, "Action", 1);
            Set(rule, "Protocol", 256);
            Set(rule, "Profiles", 0x7FFFFFFF);
            Set(rule, "InterfaceTypes", "All");
            rules!.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, rules, new object[] { rule });
        }
        finally
        {
            Release(rule);
            Release(rules);
            Release(policy);
        }
    }

    public static void RemoveAllowRule()
    {
        object? policy = null;
        object? rules = null;
        try
        {
            policy = CreatePolicy();
            rules = Get(policy, "Rules");
            rules!.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, rules, new object[] { RuleName });
        }
        catch
        {
            // Already gone.
        }
        finally
        {
            Release(rules);
            Release(policy);
        }
    }

    private static object? Get(object target, string name, params object[] args)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args.Length == 0 ? null : args);

    private static void Set(object target, string name, object value)
        => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value });

    private static object CreatePolicy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows Firewall is unavailable.");
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.ReleaseComObject(com);
    }

    private static void Save(FirewallSnapshot snapshot)
    {
        Directory.CreateDirectory(Folder);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, snapshot.Domain + "\n" + snapshot.Private + "\n" + snapshot.Public);
        File.Move(temp, StatePath, overwrite: true);
    }

    private static FirewallSnapshot Load()
    {
        var lines = File.ReadAllLines(StatePath);
        if (lines.Length < 3 ||
            !bool.TryParse(lines[0], out var domain) ||
            !bool.TryParse(lines[1], out var privacy) ||
            !bool.TryParse(lines[2], out var publicOn))
        {
            throw new InvalidOperationException("The saved firewall settings could not be read.");
        }

        return new FirewallSnapshot(domain, privacy, publicOn);
    }

    private static void DeleteState()
    {
        try { File.Delete(StatePath); } catch { /* next launch retries */ }
    }

    private static void Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start " + file);
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private static string Describe(FirewallSnapshot now)
    {
        var off = new List<string>();
        if (!now.Domain) off.Add("Domain");
        if (!now.Private) off.Add("Private");
        if (!now.Public) off.Add("Public");
        if (off.Count == 3)
            return "Windows Firewall is off for Domain, Private, and Public. Inbound traffic is allowed. Stop turns the previous firewall settings back on.";
        if (off.Count > 0)
            return "Windows Firewall is off for " + string.Join(", ", off) + ". Inbound traffic is allowed. Stop restores the previous settings.";
        return "Inbound traffic is allowed for this session. Windows kept the firewall profiles on. Stop removes the allow rule.";
    }

    private readonly record struct Profile(int Id, string Label, string Netsh);

    private sealed record FirewallSnapshot(bool Domain, bool Private, bool Public);
}
