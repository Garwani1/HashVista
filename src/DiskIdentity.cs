// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HashVista;

/// <summary>
/// Resolves a file's volume to its physical disk(s), then reads the serial reported
/// by Windows storage drivers. Results and failures are cached per volume.
/// </summary>
public sealed class DiskIdentityResolver
{
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private readonly Dictionary<string, DiskIdentityInfo> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiskIdentityInfo> directoryCache = new(StringComparer.OrdinalIgnoreCase);

    // The scanner supplies a final physical path, including the resolved leaf
    // target for file reparse points. This keeps the directory cache valid across
    // cross-volume file links without opening every ordinary file again here.
    public DiskIdentityInfo Resolve(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory != null && directoryCache.TryGetValue(directory, out var directoryIdentity))
            return directoryIdentity;
        var identity = ResolveUncached(fullPath);
        if (directory != null) directoryCache[directory] = identity;
        return identity;
    }

    private DiskIdentityInfo ResolveUncached(string fullPath)
    {
        string fallbackRoot = Path.GetPathRoot(fullPath) ?? fullPath;
        if (fallbackRoot.StartsWith(@"\\", StringComparison.Ordinal) &&
            !fallbackRoot.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return Cache(fallbackRoot, new DiskIdentityInfo
            {
                VolumeRoot = fallbackRoot, Status = "UnsupportedNetworkPath",
                Error = "A physical disk serial is not available for a network share."
            });
        }

        var mount = new StringBuilder(32768);
        if (!GetVolumePathNameW(fullPath, mount, (uint)mount.Capacity))
        {
            return Cache(fallbackRoot, new DiskIdentityInfo
            {
                VolumeRoot = fallbackRoot, Status = "VolumeLookupFailed",
                Error = new Win32Exception(Marshal.GetLastWin32Error()).Message
            });
        }

        string volumeRoot = mount.ToString();
        if (cache.TryGetValue(volumeRoot, out var cached)) return cached;
        var identity = new DiskIdentityInfo { VolumeRoot = volumeRoot };
        try
        {
            var volumeName = new StringBuilder(1024);
            string devicePath = GetVolumeNameForVolumeMountPointW(volumeRoot, volumeName, (uint)volumeName.Capacity)
                ? volumeName.ToString().TrimEnd('\\')
                : ToDevicePath(volumeRoot);
            if (devicePath.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
                identity.VolumeGuidPath = devicePath + @"\";

            using var volume = Open(devicePath);
            if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            int[] diskNumbers = GetDiskNumbers(volume);
            identity.DiskNumber = string.Join(";", diskNumbers.Distinct().OrderBy(x => x));
            var serials = new List<string>();
            var models = new List<string>();
            var errors = new List<string>();
            bool anyMissingSerial = false;
            foreach (int diskNumber in diskNumbers.Distinct())
            {
                try
                {
                    using var disk = Open($@"\\.\PhysicalDrive{diskNumber}");
                    if (disk.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var descriptor = ReadDeviceDescriptor(disk);
                    if (!string.IsNullOrWhiteSpace(descriptor.Serial)) serials.Add(descriptor.Serial);
                    else anyMissingSerial = true;
                    if (!string.IsNullOrWhiteSpace(descriptor.Model)) models.Add(descriptor.Model);
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException)
                {
                    anyMissingSerial = true;
                    errors.Add($"PhysicalDrive{diskNumber}: {ex.Message}");
                }
            }
            identity.DiskSerialNumber = string.Join(";", serials.Distinct(StringComparer.OrdinalIgnoreCase));
            identity.DiskModel = string.Join(" | ", models.Distinct(StringComparer.OrdinalIgnoreCase));
            identity.Error = string.Join(" | ", errors);
            identity.Status = serials.Count == 0
                ? (anyMissingSerial || errors.Count > 0 ? "SerialUnavailable" : "NoExtents")
                : anyMissingSerial || errors.Count > 0 ? "Partial" : "Ready";
            if (serials.Count > 0 && string.IsNullOrEmpty(identity.Error) && anyMissingSerial)
                identity.Error = "At least one physical disk did not report a serial number.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException)
        {
            identity.Status = "SerialQueryFailed";
            identity.Error = ex.Message;
        }
        return Cache(volumeRoot, identity);
    }

    private DiskIdentityInfo Cache(string key, DiskIdentityInfo identity)
    {
        cache[key] = identity;
        return identity;
    }

    private static string ToDevicePath(string volumeRoot)
    {
        string trimmed = volumeRoot.TrimEnd('\\');
        return trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? trimmed
            : @"\\.\" + trimmed;
    }

    private static SafeFileHandle Open(string path) => CreateFileW(
        path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

    private static int[] GetDiskNumbers(SafeFileHandle volume)
    {
        // 32 bytes fits the header and one DISK_EXTENT in a single call.
        byte[] output = new byte[32];
        while (true)
        {
            bool ok = DeviceIoControl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                null, 0, output, output.Length, out _, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!ok && error is not (234 or 122)) // ERROR_MORE_DATA or ERROR_INSUFFICIENT_BUFFER
                throw new Win32Exception(error);
            int count = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(0, 4));
            if (count <= 0 || count > 4096)
                throw new InvalidDataException("The volume returned an invalid physical disk extent count.");
            int required = 8 + count * 24;
            if (!ok || required > output.Length)
            {
                output = new byte[Math.Max(required, output.Length * 2)];
                continue;
            }
            var numbers = new int[count];
            for (int i = 0; i < count; i++)
                numbers[i] = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8 + i * 24, 4));
            return numbers;
        }
    }

    private static (string Serial, string Model) ReadDeviceDescriptor(SafeFileHandle disk)
    {
        // STORAGE_PROPERTY_QUERY: StorageDeviceProperty + PropertyStandardQuery.
        byte[] query = new byte[12];
        byte[] header = new byte[8];
        if (!DeviceIoControl(disk, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length,
            header, header.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (size < 36 || size > 1024 * 1024)
            throw new InvalidDataException("The storage driver returned an invalid descriptor size.");
        byte[] descriptor = new byte[size];
        if (!DeviceIoControl(disk, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length,
            descriptor, descriptor.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int length = Math.Min(returned, descriptor.Length);
        string vendor = ReadOffsetString(descriptor, length, 12);
        string product = ReadOffsetString(descriptor, length, 16);
        string serial = ReadOffsetString(descriptor, length, 24);
        return (serial, string.Join(" ", new[] { vendor, product }.Where(x => x.Length > 0)));
    }

    private static string ReadOffsetString(byte[] buffer, int length, int offsetPosition)
    {
        int offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offsetPosition, 4));
        if (offset <= 0 || offset >= length) return "";
        int end = offset;
        while (end < length && buffer[end] != 0) end++;
        string text = Encoding.ASCII.GetString(buffer, offset, end - offset);
        // Keep the serial exactly as reported, minus control/non-printable characters.
        return new string(text.Where(c => c >= 0x20 && c <= 0x7E).ToArray()).Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        byte[]? input, int inputLength, byte[] output, int outputLength,
        out int bytesReturned, IntPtr overlapped);
}
