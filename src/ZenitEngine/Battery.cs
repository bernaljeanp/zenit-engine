using System.Management;

// Battery charge limit through Dell's BIOS attributes over WMI (root\dcim\sysman\biosattributes, the interface Dell
// Command | Configure uses and Linux's dell-wmi-sysman reads). "Custom" charges from CustomChargeStart % up to
// CustomChargeStop %; Zenit offers a stop limit and resumes charging 5 points below it. "Adaptive" is Dell's default,
// which picks the limit from how the laptop is used. Needs admin (the Engine) and no BIOS admin password.
// Verified on the Alienware 16X Aurora: start 50-95, stop 55-100.
static class Battery
{
    const string Ns = @"root\dcim\sysman\biosattributes";

    public record Charge(string Mode, int Start, int Stop);

    public static Charge Read() => new(Text(Query("EnumerationAttribute", "PrimaryBattChargeCfg")["CurrentValue"]),
        Convert.ToInt32(Query("IntegerAttribute", "CustomChargeStart")["CurrentValue"]),
        Convert.ToInt32(Query("IntegerAttribute", "CustomChargeStop")["CurrentValue"]));

    // null: Dell's Adaptive mode. Otherwise charging stops at limit %.
    public static void Set(int? limit)
    {
        if (limit is null) { Apply("PrimaryBattChargeCfg", "Adaptive"); return; }
        var (start, stop) = Thresholds(limit.Value);
        // The BIOS keeps start below stop at every step: raise the stop before the start, lower the start before the stop.
        if (stop > Read().Start) { Apply("CustomChargeStop", $"{stop}"); Apply("CustomChargeStart", $"{start}"); }
        else { Apply("CustomChargeStart", $"{start}"); Apply("CustomChargeStop", $"{stop}"); }
        Apply("PrimaryBattChargeCfg", "Custom");
    }

    public static (int Start, int Stop) Thresholds(int limit)
    {
        var stop = Math.Clamp(limit, 55, 100);
        return (Math.Clamp(stop - 5, 50, 95), stop);
    }

    static string Text(object value) => value is string[] a ? a[0] : (string)value;

    static ManagementBaseObject Query(string cls, string attribute)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Ns, $"SELECT * FROM {cls} WHERE AttributeName='{attribute}'");
            return searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault()
                ?? throw new PlatformNotSupportedException("this laptop's BIOS has no battery charge settings");
        }
        catch (ManagementException e) when (e.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotFound)
        {
            throw new PlatformNotSupportedException("no Dell BIOS settings interface (not a Dell or Alienware?)", e);
        }
    }

    static void Apply(string attribute, string value)
    {
        using var searcher = new ManagementObjectSearcher(Ns, "SELECT * FROM BIOSAttributeInterface");
        using var bios = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
            ?? throw new PlatformNotSupportedException("no Dell BIOS settings interface");
        using var p = bios.GetMethodParameters("SetAttribute");
        p["AttributeName"] = attribute;
        p["AttributeValue"] = value;
        p["SecType"] = 0u; p["SecHndCount"] = 0u; p["SecHandle"] = Array.Empty<byte>(); // no BIOS admin password
        using var r = bios.InvokeMethod("SetAttribute", p, null);
        var status = Convert.ToInt32(r["Status"]);
        // Dell's status codes: 1 failed, 2 invalid parameter, 3 access denied (a BIOS password is set), 4 not supported.
        if (status != 0) throw new InvalidOperationException(status == 3
            ? Lang.T("The BIOS refused the change: a BIOS administrator password is set.", "La BIOS rechazó el cambio: tiene contraseña de administrador.")
            : Lang.T($"The BIOS refused {attribute}={value} (status {status}).", $"La BIOS rechazó {attribute}={value} (estado {status})."));
    }
}
