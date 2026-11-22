namespace Voltra.Linux;

internal sealed record LinuxSerialPort(string Path, string Node, string? Vendor, string? Product, string HardwarePath, string? UsbSocket = null)
{
    internal bool IsCandidate => Vendor is not null && Product is not null
        ? Vendor.Equals("303a", StringComparison.OrdinalIgnoreCase) && Product.Equals("1001", StringComparison.OrdinalIgnoreCase)
        : Path.Contains("/serial/by-id/", StringComparison.Ordinal) &&
          System.IO.Path.GetFileName(Path).StartsWith("usb-Espressif_USB_JTAG_serial_debug_unit", StringComparison.OrdinalIgnoreCase);

    internal string Description => System.IO.Path.GetFileName(Node) + (Path == Node ? "" : " (by-id)") +
        (Vendor is not null && Product is not null ? $" {Vendor}:{Product}" : " (USB ID unavailable)");
}

internal sealed class LinuxSerialDiscovery(string dev = "/dev", string sys = "/sys")
{
    internal LinuxSerialPort[] Scan()
    {
        var ports = new List<LinuxSerialPort>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string alias in Entries(Path.Combine(dev, "serial", "by-id"), "*"))
        {
            string? node = LinkTarget(alias, final: false);
            if (node is not null && !IsSerialNode(node)) node = LinkTarget(alias);
            if (node is null || Path.GetDirectoryName(node) != dev || !IsSerialNode(node) ||
                !File.Exists(node) || !seen.Add(node)) continue;
            ports.Add(Describe(alias, node));
        }

        foreach (string pattern in new[] { "ttyACM*", "ttyUSB*" })
            foreach (string node in Entries(dev, pattern))
                if (seen.Add(node)) ports.Add(Describe(node, node));
        return ports.ToArray();
    }

    private LinuxSerialPort Describe(string path, string node)
    {
        string entry = Path.Combine(sys, "class", "tty", Path.GetFileName(node));
        // A relative "device" link must be read from the canonical tty location.
        // Resolving it under the /sys/class alias makes its ".." refer to the class tree.
        string? tty = LinkTarget(entry) ?? (Directory.Exists(entry) ? entry : null);
        if (tty is null) return new(path, node, null, null, entry);

        var parent = new DirectoryInfo(LinkTarget(Path.Combine(tty, "device")) ?? tty);
        for (DirectoryInfo? directory = parent; directory is not null && directory.FullName != sys; directory = directory.Parent)
        {
            string? vendor = ReadAttribute(directory, "idVendor");
            string? product = ReadAttribute(directory, "idProduct");
            if (vendor is not null && product is not null)
            {
                string generation = ReadAttribute(directory, "devnum") ?? "";
                bool native = vendor.Equals("303a", StringComparison.OrdinalIgnoreCase) && product.Equals("1001", StringComparison.OrdinalIgnoreCase);
                return new(path, node, vendor, product, $"{directory.FullName}:{generation}", native ? directory.FullName : null);
            }
        }
        return new(path, node, null, null, tty);
    }

    private static bool IsSerialNode(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("ttyACM", StringComparison.Ordinal) || name.StartsWith("ttyUSB", StringComparison.Ordinal);
    }

    private static string[] Entries(string directory, string pattern)
    {
        try { return Directory.GetFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static string? LinkTarget(string path, bool final = true)
    {
        // Use the file API for both kinds of links. The result is a path, not a file type.
        try { return File.ResolveLinkTarget(path, returnFinalTarget: final)?.FullName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadAttribute(DirectoryInfo directory, string name)
    {
        string path = Path.Combine(directory.FullName, name);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
