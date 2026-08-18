using System;
using System.Collections.Generic;
using System.Linq;

namespace BeastsV3.Analytics;

// Encodes and decodes pathofexile.com atlas skill tree URLs. Ported from the
// PassiveSkillTreePlanter plugin, which credits https://github.com/EmmittJ/PoESkillTree.
// Storing the URL alongside the raw node ids makes a recorded map reviewable in a browser.
public static class AtlasTreeUrl
{
    private const string AtlasPrefix = "https://www.pathofexile.com/fullscreen-atlas-skill-tree/";

    // Byte layout: [version:4][class:1][ascendancy:1][nodeCount:1][nodes:2*n][tail:2]
    private const int VersionLength = 4;
    private const int HeaderLength = VersionLength + 3;

    // Builds a shareable atlas tree URL from an allocated node set.
    public static string Encode(IEnumerable<ushort> nodes)
    {
        var ordered = (nodes ?? []).Distinct().OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return string.Empty;

        // The node count field is a single byte. An atlas tree never approaches 255
        // points, but truncate rather than emit a corrupt code if it somehow does.
        if (ordered.Length > byte.MaxValue) ordered = ordered.Take(byte.MaxValue).ToArray();

        var bytes = new List<byte>(HeaderLength + ordered.Length * 2 + 2)
        {
            0, 0, 0, 6,             // version 6
            0,                      // class
            0,                      // ascendancy
            (byte)ordered.Length,   // node count
        };

        foreach (var node in ordered)
        {
            bytes.Add((byte)(node >> 8));
            bytes.Add((byte)node);
        }

        bytes.Add(0);
        bytes.Add(0);

        var encoded = Convert.ToBase64String(bytes.ToArray())
            .Replace('+', '-')
            .Replace('/', '_');

        return AtlasPrefix + encoded;
    }

    // Parses an atlas tree URL or bare build code back into node ids. Returns false on
    // anything malformed rather than throwing - this parses user-pasted input.
    public static bool TryDecode(string urlOrCode, out ushort[] nodes)
    {
        nodes = [];
        if (string.IsNullOrWhiteSpace(urlOrCode)) return false;

        var code = urlOrCode.Trim();

        var slash = code.LastIndexOf('/');
        if (slash >= 0 && slash < code.Length - 1) code = code[(slash + 1)..];

        code = code.Replace('-', '+').Replace('_', '/');

        // Restore base64 padding, which the URL form omits.
        var remainder = code.Length % 4;
        if (remainder == 1) return false;
        if (remainder > 0) code = code.PadRight(code.Length + (4 - remainder), '=');

        byte[] data;
        try
        {
            data = Convert.FromBase64String(code);
        }
        catch (FormatException)
        {
            return false;
        }

        if (data.Length < VersionLength) return false;

        var version = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        var start = version > 3 ? HeaderLength : VersionLength + 2;
        if (data.Length < start) return false;

        var result = new List<ushort>((data.Length - start) / 2);
        for (var i = start; i + 1 < data.Length; i += 2)
        {
            var id = (ushort)((data[i] << 8) | data[i + 1]);

            // The two-byte tail encodes as node id 0, which is not a real node.
            if (id != 0) result.Add(id);
        }

        nodes = result.Distinct().OrderBy(x => x).ToArray();
        return nodes.Length > 0;
    }
}
