using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FinSight.Tests.TestHelpers;

/// <summary>
/// Writes a small password-protected, text-based PDF statement (standard security handler, revision 3, 128-bit RC4), generated
/// in the test so no binary fixture is committed. PdfPig reads encrypted PDFs but can't write them, so the encryption
/// (PDF 1.7 section 7.6.3, algorithms 1, 2, 3 and 5) is done here.
/// </summary>
internal static class EncryptedPdfBuilder
{
    private const double FontSize = 9;

    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>Helvetica advance widths (per 1000 em) for the characters right-aligned cells use.</summary>
    private static readonly Dictionary<char, int> Widths = new()
    {
        ['0'] = 556, ['1'] = 556, ['2'] = 556, ['3'] = 556, ['4'] = 556, ['5'] = 556, ['6'] = 556, ['7'] = 556, ['8'] = 556, ['9'] = 556,
        [','] = 278, ['.'] = 278, [' '] = 278, ['W'] = 944, ['i'] = 222, ['t'] = 278, ['h'] = 556, ['d'] = 556, ['r'] = 333, ['a'] = 556,
        ['w'] = 722, ['l'] = 222, ['s'] = 500, ['D'] = 722, ['e'] = 556, ['p'] = 556, ['o'] = 556, ['B'] = 667, ['c'] = 500, ['n'] = 556,
    };

    /// <summary>The same chequing statement as <see cref="PdfStatementBuilder.SampleChequingStatement"/>, locked with <paramref name="userPassword"/>.</summary>
    public static byte[] Chequing(string userPassword, string ownerPassword = "owner-secret-not-used")
    {
        var lines = new List<(double X, string Text)[]>
        {
            new[] { (40d, "Maple Credit Union") },
            new[] { (40d, "Everyday Chequing Account") },
            new[] { (40d, "Account number: 000123-4567890") },
            new[] { (40d, "Statement period: August 1, 2026 to August 31, 2026") },
            new[] { (40d, "Date"), (100d, "Description"), (-400d, "Withdrawals"), (-480d, "Deposits"), (-570d, "Balance") },
            new[] { (40d, "Aug 1"), (100d, "Opening balance"), (-570d, "1,500.00") },
            new[] { (40d, "Aug 3"), (100d, "PAYROLL DEPOSIT ACME CORP"), (-480d, "3,100.00"), (-570d, "4,600.00") },
            new[] { (40d, "Aug 4"), (100d, "NETFLIX.COM"), (-400d, "20.99"), (-570d, "4,579.01") },
            new[] { (40d, "Aug 6"), (100d, "POS PURCHASE LOBLAWS #221"), (-400d, "142.30"), (-570d, "4,436.71") },
            new[] { (40d, "Aug 31"), (100d, "Closing balance"), (-570d, "4,436.71") },
        };

        var content = new StringBuilder();
        var y = 750.0;
        foreach (var cells in lines)
        {
            foreach (var (x, text) in cells)
            {
                var left = x >= 0 ? x : -x - (text.Sum(c => Widths[c]) * FontSize / 1000);
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 9 Tf {left:0.###} {y:0.###} Td ({text.Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)}) Tj ET\n");
            }

            y -= 14;
        }

        return Build(Encoding.ASCII.GetBytes(content.ToString()), userPassword, ownerPassword);
    }

    private static byte[] Build(byte[] contentStream, string userPassword, string ownerPassword)
    {
        const int permissions = -4;
        var id = RandomNumberGenerator.GetBytes(16);
        var owner = OwnerEntry(ownerPassword, userPassword);
        var key = FileKey(userPassword, owner, permissions, id);
        var user = UserEntry(key, id);

        var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Object(int number, string body)
        {
            offsets.Add(output.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Object(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var encrypted = Rc4(ObjectKey(key, 5), contentStream);
        offsets.Add(output.Position);
        Write($"5 0 obj\n<< /Length {encrypted.Length} >>\nstream\n");
        output.Write(encrypted);
        Write("\nendstream\nendobj\n");

        Object(6, $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P {permissions} /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> >>");

        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        var hexId = Convert.ToHexString(id);
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt 6 0 R /ID [<{hexId}> <{hexId}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] Pad(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        return bytes.Take(32).Concat(Padding).Take(32).ToArray();
    }

    /// <summary>Algorithm 3: the /O entry.</summary>
    private static byte[] OwnerEntry(string ownerPassword, string userPassword)
    {
#pragma warning disable CA5351 // MD5 and RC4 are what the PDF format specifies; this only builds a test fixture.
        var hash = MD5.HashData(Pad(ownerPassword));
        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(hash);
        }

        var result = Rc4(hash, Pad(userPassword));
        for (var i = 1; i <= 19; i++)
        {
            result = Rc4(hash.Select(b => (byte)(b ^ i)).ToArray(), result);
        }

        return result;
    }

    /// <summary>Algorithm 2: the file encryption key.</summary>
    private static byte[] FileKey(string userPassword, byte[] owner, int permissions, byte[] id)
    {
        var input = Pad(userPassword).Concat(owner).Concat(BitConverter.GetBytes(permissions)).Concat(id).ToArray();
        var hash = MD5.HashData(input);
        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(hash);
        }

        return hash;
    }

    /// <summary>Algorithm 5: the /U entry for revision 3.</summary>
    private static byte[] UserEntry(byte[] key, byte[] id)
    {
        var result = Rc4(key, MD5.HashData(Padding.Concat(id).ToArray()));
        for (var i = 1; i <= 19; i++)
        {
            result = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), result);
        }

        return result.Concat(new byte[16]).ToArray();
    }

    /// <summary>Algorithm 1: the key for one object's strings and streams.</summary>
    private static byte[] ObjectKey(byte[] key, int objectNumber) =>
        MD5.HashData(key.Concat(new[] { (byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), (byte)0, (byte)0 }).ToArray());
#pragma warning restore CA5351

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }
}
