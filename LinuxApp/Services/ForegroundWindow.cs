using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Voltra.Linux;

internal sealed record Focus(int Pid = 0, string AppId = "", string Window = "", bool Supported = true);

internal sealed class ForegroundWindow : IDisposable
{
    private readonly bool wayland = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") switch
    {
        "wayland" => true,
        "x11" => false,
        _ => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
    });
    private readonly bool sway = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK"));
    private nint connection;
    private uint root;
    private readonly Dictionary<string, uint> atoms = [];
    private long nextRead;
    private Focus last = new();

    internal async Task<Focus> ReadAsync(bool fresh, CancellationToken token)
    {
        long now = Environment.TickCount64;
        if (!fresh && now < nextRead) return last;
        try
        {
            if (wayland)
            {
                last = sway ? ParseSway(await ProcessCommand.RunAsync("swaymsg", token, "-r", "-t", "get_tree")) : new(Supported: false);
            }
            else last = ReadX11();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or
            TimeoutException or Win32Exception or JsonException or InvalidOperationException)
        { last = new(Supported: false); }
        nextRead = Environment.TickCount64 + (last.Supported ? wayland ? 350 : 100 : 2500);
        return last;
    }

    internal static Focus ParseSway(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Find(doc.RootElement) ?? new();
        static Focus? Find(JsonElement node)
        {
            if (node.TryGetProperty("focused", out var flag) && flag.ValueKind == JsonValueKind.True)
            {
                int pid = node.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : 0;
                string id = node.TryGetProperty("app_id", out var app) && app.ValueKind == JsonValueKind.String ? app.GetString()! : "";
                return new(pid, id, node.TryGetProperty("id", out var window) ? window.ToString() : "");
            }
            foreach (var field in new[] { "nodes", "floating_nodes" })
                if (node.TryGetProperty(field, out var children))
                    foreach (var child in children.EnumerateArray())
                        if (Find(child) is { } focus) return focus;
            return null;
        }
    }

    private Focus ReadX11()
    {
        if (connection == 0)
        {
            connection = xcb_connect(0, out int screen);
            if (xcb_connection_has_error(connection) != 0) { Dispose(); return new(Supported: false); }
            var iterator = xcb_setup_roots_iterator(xcb_get_setup(connection));
            while (screen-- > 0 && iterator.Remaining > 0) xcb_screen_next(ref iterator);
            if (iterator.Remaining == 0) { Dispose(); return new(Supported: false); }
            root = (uint)Marshal.ReadInt32(iterator.Data);
        }
        uint window = Scalar(root, "_NET_ACTIVE_WINDOW", 33); // WINDOW
        if (xcb_connection_has_error(connection) != 0) { Dispose(); return new(Supported: false); }
        if (window == 0) return new();
        var type = Property(window, "_NET_WM_WINDOW_TYPE", 4, 32);
        for (int i = 0; i + 4 <= type.Length; i += 4)
            if (BitConverter.ToUInt32(type, i) == Atom("_NET_WM_WINDOW_TYPE_DESKTOP") ||
                BitConverter.ToUInt32(type, i) == Atom("_NET_WM_WINDOW_TYPE_DOCK")) return new();
        int pid = (int)Scalar(window, "_NET_WM_PID", 6); // CARDINAL
        string app = Encoding.UTF8.GetString(Property(window, "_GTK_APPLICATION_ID", Atom("UTF8_STRING"), 8)).TrimEnd('\0');
        return new(pid, app, window.ToString());
    }

    private uint Atom(string name)
    {
        if (atoms.TryGetValue(name, out var atom)) return atom;
        uint request = xcb_intern_atom(connection, 0, (ushort)name.Length, name);
        nint reply = xcb_intern_atom_reply(connection, request, 0);
        try { return atoms[name] = reply == 0 ? 0 : (uint)Marshal.ReadInt32(reply, 8); }
        finally { free(reply); }
    }

    private uint Scalar(uint window, string name, uint type)
    {
        var bytes = Property(window, name, type, 32);
        return bytes.Length == 4 ? BitConverter.ToUInt32(bytes) : 0;
    }

    private byte[] Property(uint window, string name, uint type, byte format)
    {
        uint cookie = xcb_get_property(connection, 0, window, Atom(name), type, 0, 256);
        nint reply = xcb_get_property_reply(connection, cookie, 0);
        try
        {
            if (reply == 0 || Marshal.ReadByte(reply, 1) != format || (uint)Marshal.ReadInt32(reply, 8) != type) return [];
            int length = xcb_get_property_value_length(reply);
            if (length is <= 0 or > 1024) return [];
            byte[] result = new byte[length];
            Marshal.Copy(xcb_get_property_value(reply), result, 0, length);
            return result;
        }
        finally { free(reply); }
    }

    public void Dispose()
    {
        if (connection != 0) xcb_disconnect(connection);
        connection = 0;
        atoms.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Iterator { internal nint Data; internal int Remaining, Index; }
    private const string Xcb = "libxcb.so.1";
    [DllImport(Xcb)] private static extern nint xcb_connect(nint display, out int screen);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(nint connection);
    [DllImport(Xcb)] private static extern void xcb_disconnect(nint connection);
    [DllImport(Xcb)] private static extern nint xcb_get_setup(nint connection);
    [DllImport(Xcb)] private static extern Iterator xcb_setup_roots_iterator(nint setup);
    [DllImport(Xcb)] private static extern void xcb_screen_next(ref Iterator iterator);
    [DllImport(Xcb)] private static extern uint xcb_intern_atom(nint connection, byte onlyExisting, ushort length, string name);
    [DllImport(Xcb)] private static extern nint xcb_intern_atom_reply(nint connection, uint cookie, nint error);
    [DllImport(Xcb)] private static extern uint xcb_get_property(nint connection, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [DllImport(Xcb)] private static extern nint xcb_get_property_reply(nint connection, uint cookie, nint error);
    [DllImport(Xcb)] private static extern nint xcb_get_property_value(nint reply);
    [DllImport(Xcb)] private static extern int xcb_get_property_value_length(nint reply);
    [DllImport("libc")] private static extern void free(nint pointer);
}
