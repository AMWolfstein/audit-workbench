using System.Text;
using System.Text.Json;

namespace AuditWorkbench.Domain.FinancialImports;

/// <summary>
/// Internal field of an imported dataset. Client files name their columns
/// differently (Account / GL Code / Ledger Account / DR / Debit ...), so the
/// importer maps the client's columns onto these stable internal fields.
/// </summary>
public sealed record ImportFieldDefinition(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    bool Required = false);

public static class TbFields
{
    public const string AccountCode = "account_code";
    public const string AccountName = "account_name";
    public const string Debit = "debit";
    public const string Credit = "credit";
    public const string Balance = "balance";
    public const string Currency = "currency";
    public const string CostCenter = "cost_center";
    public const string AccountGroup = "account_group";

    public static readonly IReadOnlyList<ImportFieldDefinition> Definitions = new[]
    {
        new ImportFieldDefinition(AccountCode, "Account code",
            new[] { "account", "account code", "account no", "account number", "gl code", "glcode", "gl account",
                    "ledger account", "ledger code", "code", "acct", "gl account code", "nominal", "nominal code" },
            Required: true),
        new ImportFieldDefinition(AccountName, "Account name",
            new[] { "account name", "description", "name", "account description", "particulars", "narration",
                    "ledger account name", "account title", "title" }),
        new ImportFieldDefinition(Debit, "Debit",
            new[] { "debit", "dr", "dr amount", "debit amount", "debit balance", "debit(dr)", "period debit" }),
        new ImportFieldDefinition(Credit, "Credit",
            new[] { "credit", "cr", "cr amount", "credit amount", "credit balance", "credit(cr)", "period credit" }),
        new ImportFieldDefinition(Balance, "Balance",
            new[] { "balance", "closing balance", "net balance", "net", "amount", "balance amount", "ending balance",
                    "closing", "cl balance" }),
        new ImportFieldDefinition(Currency, "Currency",
            new[] { "currency", "curr", "ccy", "currency code", "cur" }),
        new ImportFieldDefinition(CostCenter, "Cost center",
            new[] { "cost center", "cost centre", "cc", "department", "dept", "cost code", "profit center" }),
        new ImportFieldDefinition(AccountGroup, "Account group",
            new[] { "account group", "group", "category", "class", "statement group", "fs line",
                    "financial statement line", "head", "account class" }),
    };
}

public static class GlFields
{
    public const string JournalNumber = "journal_number";
    public const string JournalSource = "journal_source";
    public const string TransactionDate = "transaction_date";
    public const string PostingDate = "posting_date";
    public const string AccountCode = "account_code";
    public const string AccountName = "account_name";
    public const string Description = "description";
    public const string Debit = "debit";
    public const string Credit = "credit";
    public const string Amount = "amount";
    public const string Currency = "currency";
    public const string Reference = "reference";
    public const string LineNumber = "line_number";
    public const string Preparer = "preparer";

    public static readonly IReadOnlyList<ImportFieldDefinition> Definitions = new[]
    {
        new ImportFieldDefinition(JournalNumber, "Journal / entry number",
            new[] { "journal no", "journal number", "journal", "document no", "document number", "doc no",
                    "voucher no", "voucher number", "voucher", "entry id", "entry no", "transaction id",
                    "transaction no", "batch no", "batch", "gl entry id" }),
        new ImportFieldDefinition(JournalSource, "Journal / source",
            new[] { "journal source", "journal type", "source", "source system", "module", "batch type",
                    "transaction type", "doc type", "document type" }),
        new ImportFieldDefinition(TransactionDate, "Transaction date",
            new[] { "date", "transaction date", "txn date", "tx date", "gl date", "entry date", "document date",
                    "value date", "accounting date" }),
        new ImportFieldDefinition(PostingDate, "Posting date",
            new[] { "posting date", "post date", "posted date", "posting", "system date", "created date" }),
        new ImportFieldDefinition(AccountCode, "Account code",
            new[] { "account", "account code", "account no", "account number", "gl code", "glcode", "gl account",
                    "ledger account", "ledger code", "code", "acct", "nominal", "nominal code" },
            Required: true),
        new ImportFieldDefinition(AccountName, "Account name",
            new[] { "account name", "account description", "account title", "ledger account name" }),
        new ImportFieldDefinition(Description, "Description",
            new[] { "description", "narration", "memo", "particulars", "text", "details", "line description",
                    "transaction description", "remark", "remarks" }),
        new ImportFieldDefinition(Debit, "Debit",
            new[] { "debit", "dr", "dr amount", "debit amount", "debit value" }),
        new ImportFieldDefinition(Credit, "Credit",
            new[] { "credit", "cr", "cr amount", "credit amount", "credit value" }),
        new ImportFieldDefinition(Amount, "Amount (signed)",
            new[] { "amount", "net amount", "value", "signed amount", "transaction amount" }),
        new ImportFieldDefinition(Currency, "Currency",
            new[] { "currency", "curr", "ccy", "currency code" }),
        new ImportFieldDefinition(Reference, "Reference",
            new[] { "reference", "ref", "ref no", "invoice no", "invoice number", "external reference",
                    "document reference", "source reference" }),
        new ImportFieldDefinition(LineNumber, "Line number",
            new[] { "line", "line no", "line number", "line id", "item", "item no", "seq", "sequence",
                    "sequence no", "row no", "entry line" }),
        new ImportFieldDefinition(Preparer, "Prepared by",
            new[] { "user", "user id", "prepared by", "preparer", "entered by", "created by", "posted by",
                    "journal owner" }),
    };

    public static IReadOnlyList<ImportFieldDefinition> For(string datasetKind) =>
        datasetKind == Domain.FinancialData.FinancialDatasetKind.GeneralLedger ? Definitions : TbFields.Definitions;
}

/// <summary>
/// Maps internal field keys onto zero-based source column indexes. Stored as
/// canonical JSON on the import so the mapping that produced a dataset is part
/// of its provenance and can be repeated exactly.
/// </summary>
public sealed class ImportColumnMapping
{
    private readonly SortedDictionary<string, int> _columns = new(StringComparer.Ordinal);

    public static ImportColumnMapping Empty() => new();

    public static ImportColumnMapping From(IEnumerable<KeyValuePair<string, int>>? columns)
    {
        var mapping = new ImportColumnMapping();
        if (columns is null)
        {
            return mapping;
        }

        foreach (var (key, index) in columns)
        {
            mapping.Map(key, index);
        }

        return mapping;
    }

    public ImportColumnMapping Map(string fieldKey, int columnIndex)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            throw new ArgumentException("A mapping requires a field key.", nameof(fieldKey));
        }

        if (columnIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columnIndex), "Column indexes start at 0.");
        }

        _columns[fieldKey.Trim()] = columnIndex;
        return this;
    }

    public ImportColumnMapping Unmap(string fieldKey)
    {
        _columns.Remove(fieldKey);
        return this;
    }

    public IReadOnlyDictionary<string, int> Columns => _columns;

    public IReadOnlyList<string> MappedFields => _columns.Keys.ToList();

    public bool Has(string fieldKey) => _columns.ContainsKey(fieldKey);

    public int? ColumnIndex(string fieldKey) =>
        _columns.TryGetValue(fieldKey, out var index) ? index : null;

    public string? FieldFor(int columnIndex)
    {
        foreach (var (key, index) in _columns)
        {
            if (index == columnIndex)
            {
                return key;
            }
        }

        return null;
    }

    public int MaxColumnIndex => _columns.Count == 0 ? -1 : _columns.Values.Max();

    /// <summary>Reads one mapped cell out of a source row (missing columns are blank).</summary>
    public string? Value(IReadOnlyList<string?> row, string fieldKey)
    {
        var index = ColumnIndex(fieldKey);
        if (index is null || index.Value >= row.Count)
        {
            return null;
        }

        return row[index.Value];
    }

    /// <summary>
    /// Maps internal fields onto the client's headers using the alias lists.
    /// Matching ignores case, punctuation and surrounding spaces; every column is
    /// used at most once and the result is deterministic (first alias match wins).
    /// </summary>
    public static ImportColumnMapping Suggest(IReadOnlyList<ImportFieldDefinition> definitions,
        IReadOnlyList<string?> headers)
    {
        var mapping = new ImportColumnMapping();
        var normalized = headers
            .Select((header, index) => (Header: NormalizeHeader(header), Index: index))
            .Where(x => x.Header.Length > 0)
            .ToList();

        foreach (var field in definitions)
        {
            var candidate = new List<(int Score, int Index)>();
            foreach (var alias in field.Aliases)
            {
                var aliasKey = NormalizeHeader(alias);
                foreach (var (header, index) in normalized)
                {
                    var score = MatchScore(aliasKey, header, field.Required);
                    if (score > 0)
                    {
                        candidate.Add((score, index));
                    }
                }
            }

            var best = candidate
                .Where(c => mapping.FieldFor(c.Index) is null)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Index)
                .FirstOrDefault();
            if (best.Score > 0)
            {
                mapping.Map(field.Key, best.Index);
            }
        }

        return mapping;
    }

    public string ToJson()
    {
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var (key, index) in _columns)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(JsonSerializer.Serialize(key)).Append(':').Append(index);
            first = false;
        }

        return builder.Append('}').ToString();
    }

    public static ImportColumnMapping FromJson(string? json)
    {
        var mapping = new ImportColumnMapping();
        if (string.IsNullOrWhiteSpace(json))
        {
            return mapping;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return mapping;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var index) && index >= 0)
            {
                mapping.Map(property.Name, index);
            }
        }

        return mapping;
    }

    /// <summary>Human-readable summary used by the UI and the audit trail.</summary>
    public string Describe(IReadOnlyList<ImportFieldDefinition> definitions) =>
        string.Join(", ", _columns.Select(pair =>
            $"{definitions.FirstOrDefault(d => d.Key == pair.Key)?.DisplayName ?? pair.Key}=col{pair.Value + 1}"));

    private static int MatchScore(string aliasKey, string headerKey, bool required)
    {
        if (aliasKey.Length == 0 || headerKey.Length == 0)
        {
            return 0;
        }

        if (aliasKey == headerKey)
        {
            return 100;
        }

        // Exact match after removing trailing qualifiers such as "amount" or "(usd)".
        if (headerKey.StartsWith(aliasKey, StringComparison.Ordinal) || aliasKey.StartsWith(headerKey, StringComparison.Ordinal))
        {
            return 60;
        }

        if (headerKey.Contains(aliasKey, StringComparison.Ordinal))
        {
            return required ? 40 : 30;
        }

        return 0;
    }

    public static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(header.Length);
        foreach (var character in header.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
