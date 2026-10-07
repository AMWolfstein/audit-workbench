using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuditWorkbench.Domain.FinancialData;

namespace AuditWorkbench.Domain.FinancialImports;

/// <summary>
/// Transaction and line identity.
/// <para>
/// Two different concepts are kept apart on purpose:
/// </para>
/// <list type="bullet">
///   <item><description>
///   the <b>database identity</b> (gl_journal_id / gl_line_id / tb_line_id)
///   identifies the stored row; it is generated per import and is not stable
///   across snapshots;
///   </description></item>
///   <item><description>
///   the <b>source identity</b> (journal_identity / line_identity) identifies the
///   client's transaction. It is either the mapped source key
///   (<c>SRC:</c>-prefixed) or a deterministic composite fingerprint
///   (<c>DRV:</c>-prefixed) derived from stable source attributes.
///   </description></item>
/// </list>
/// <para>
/// The future roll-forward compares two snapshots on the source identity, which
/// is why the identity must not contain the values that later phases compare
/// (amount, account, date, description). Deterministic canonical hashing keeps
/// the identity identical across runs, machines and snapshots.
/// </para>
/// </summary>
public static class TransactionIdentity
{
    /// <summary>Separates the identity kind from the key so kinds can never collide.</summary>
    public const string SourcePrefix = "SRC:";

    public const string DerivedPrefix = "DRV:";

    private const string Separator = "\u001f"; // ASCII unit separator: never present in client data

    /// <summary>Canonical source key: trimmed, upper-cased, internal whitespace collapsed.</summary>
    public static string CanonicalKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var character in raw.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
            lastWasSpace = false;
        }

        return builder.ToString();
    }

    /// <summary>Identity taken from a client-supplied key (journal/document/voucher number).</summary>
    public static string FromSourceKey(string raw) => SourcePrefix + CanonicalKey(raw);

    /// <summary>
    /// Deterministic composite identity for a journal whose source supplies no
    /// stable key: the journal attributes plus the ordinal of this journal among
    /// the journals of the import that share exactly the same attributes.
    /// </summary>
    public static string DeriveJournalKey(IEnumerable<string?> attributes, int twinOrdinal)
    {
        if (twinOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(twinOrdinal), "Ordinals start at 1.");
        }

        return DerivedPrefix + "J:" + HashParts(attributes.Append(twinOrdinal.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Line identity when the source supplies no line number: the line's ordinal
    /// inside its journal. This deliberately excludes amount/account/date so a
    /// changed value stays the <em>same</em> line and is reported as a change.
    /// </summary>
    public static string DeriveLineKey(int lineOrdinal)
    {
        if (lineOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineOrdinal), "Ordinals start at 1.");
        }

        return DerivedPrefix + "L:" + lineOrdinal.ToString(CultureInfo.InvariantCulture);
    }

    public static string FromSourceLineKey(string raw) => SourcePrefix + CanonicalKey(raw);

    /// <summary>SHA-256 over the canonical (unit-separated) representation of the parts.</summary>
    public static string HashParts(IEnumerable<string?> parts)
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var part in parts)
        {
            if (!first)
            {
                builder.Append(Separator);
            }

            builder.Append(CanonicalValue(part));
            first = false;
        }

        return Sha256Hex(builder.ToString());
    }

    public static string JournalHash(IEnumerable<string?> parts) => HashParts(parts);

    /// <summary>Digest of everything that identifies and describes one ledger line.</summary>
    public static string LineHash(string journalIdentity, string lineIdentity, IEnumerable<string?> attributes,
        long debitMinor, long creditMinor)
    {
        var parts = new List<string?> { journalIdentity, lineIdentity };
        parts.AddRange(attributes);
        parts.Add(ValueHash(debitMinor, creditMinor));
        return HashParts(parts);
    }

    /// <summary>Digest of the monetary values only (roll-forward: changed amount).</summary>
    public static string ValueHash(long debitMinor, long creditMinor) =>
        HashParts(new[]
        {
            debitMinor.ToString(CultureInfo.InvariantCulture),
            creditMinor.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>Digest of the descriptive attributes only (roll-forward: changed account/date/description).</summary>
    public static string AttributeHash(params string?[] attributes) => HashParts(attributes);

    /// <summary>Digest of one imported trial-balance row.</summary>
    public static string RowHash(string accountCode, long debitMinor, long creditMinor, long balanceMinor,
        string currencyCode) =>
        HashParts(new[]
        {
            CanonicalKey(accountCode),
            debitMinor.ToString(CultureInfo.InvariantCulture),
            creditMinor.ToString(CultureInfo.InvariantCulture),
            balanceMinor.ToString(CultureInfo.InvariantCulture),
            CanonicalKey(currencyCode),
        });

    /// <summary>
    /// Import fingerprint: identifies "this dataset content, produced by this
    /// mapping, from this file". Used together with the file digest for duplicate
    /// import protection - it is an identity, not a checksum of evidence.
    /// </summary>
    public static string Fingerprint(string datasetKind, Guid engagementId, Guid financialPeriodId,
        string sourceFileSha256, string columnMappingJson, long totalDebitMinor, long totalCreditMinor, int rowCount) =>
        HashParts(new[]
        {
            datasetKind,
            engagementId.ToString("D"),
            financialPeriodId.ToString("D"),
            (sourceFileSha256 ?? string.Empty).ToLowerInvariant(),
            columnMappingJson,
            totalDebitMinor.ToString(CultureInfo.InvariantCulture),
            totalCreditMinor.ToString(CultureInfo.InvariantCulture),
            rowCount.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>Stable string used to compare values across snapshots without culture effects.</summary>
    public static string CanonicalValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string CanonicalDate(string? isoDate) =>
        string.IsNullOrWhiteSpace(isoDate) ? string.Empty : isoDate.Trim();

    public static string CanonicalAmount(long minor) => minor.ToString(CultureInfo.InvariantCulture);

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(new UTF8Encoding(false).GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>True when the identity came from the client's source key.</summary>
    public static bool IsSourceIdentity(string identity) =>
        identity.StartsWith(SourcePrefix, StringComparison.Ordinal);

    public static string IdentitySource(string identity) =>
        IsSourceIdentity(identity) ? ImportIdentitySource.Source : ImportIdentitySource.Derived;
}
