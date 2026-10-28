// SPDX-License-Identifier: GPL-3.0-or-later
namespace HashVista;

/// <summary>A value snapshot shared by result paging and filtered export.</summary>
public sealed record ResultFilter
{
    public string Kind { get; }
    // null selects every disk; an empty string selects rows with an unknown disk.
    public string? DiskNumber { get; }
    public bool IsAll => Kind == "All" && DiskNumber is null;
    public static ResultFilter All { get; } = new();

    public ResultFilter(string kind = "All", string? diskNumber = null)
    {
        Kind = string.IsNullOrWhiteSpace(kind) ? "All" : kind.Trim().ToUpperInvariant() switch
        {
            "ALL" or "ALL FILES" => "All",
            "IMAGE" or "IMAGES" => "Image",
            "VIDEO" or "VIDEOS" => "Video",
            "OTHER" => "Other",
            _ => kind
        };
        DiskNumber = diskNumber;
    }
}
