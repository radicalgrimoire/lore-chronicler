using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record RevisionHistoryEntry(ulong Number, string Revision, string? Message, string? Author, DateTimeOffset? Timestamp,
    string? AuthorName = null, string? AuthorResolutionWarning = null)
{
    public string DisplayMessage => string.IsNullOrWhiteSpace(Message) ? "No message" : Message;
    public string DisplayAuthor => !string.IsNullOrWhiteSpace(AuthorName) ? $"{AuthorName} ({Author})"
        : string.IsNullOrWhiteSpace(Author) ? "Unknown author" : Author;
    public string DisplayAuthorName => !string.IsNullOrWhiteSpace(AuthorName) ? AuthorName
        : string.IsNullOrWhiteSpace(Author) ? "Unknown author" : Author;
    public string DisplayTimestamp => Timestamp?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "Unknown date";
}

public interface IRevisionHistoryService
{
    Task<IReadOnlyList<RevisionHistoryEntry>> ReadAsync(string path);
}

public sealed class LoreRevisionHistoryService : IRevisionHistoryService
{
    public const uint MaximumEntries = 100;
    private readonly RevisionHistoryAuthorResolver authorResolver;

    public LoreRevisionHistoryService(IUserNameService? userNames = null)
    {
        authorResolver = new RevisionHistoryAuthorResolver(userNames ?? new LoreUserNameService());
    }

    public async Task<IReadOnlyList<RevisionHistoryEntry>> ReadAsync(string path)
    {
        using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
        var entries = new List<EntryBuilder>();
        var sync = new object();
        string? error = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRevisionHistoryArgs { Length = MaximumEntries };
        await Lore.RevisionHistory(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.REVISION_HISTORY_ENTRY)
                {
                    var data = ev.GetData<LoreRevisionHistoryEntryEventDataFFI>();
                    entries.Add(new EntryBuilder(data.RevisionNumber, Convert.ToHexString(data.Revision.Data)));
                }
                else if (ev.Tag == LoreEventTag.METADATA && entries.Count > 0)
                {
                    var data = ev.GetData<LoreMetadataEventDataFFI>();
                    var entry = entries[^1];
                    var value = data.Value;
                    switch (data.Key)
                    {
                        case "message":
                            if (value.Tag == LoreMetadataType.STRING) entry.Message = value.String;
                            else error = "The revision message has an invalid format.";
                            break;
                        case "created-by":
                            if (value.Tag == LoreMetadataType.STRING) entry.CreatedBy = value.String;
                            else error = "The revision author has an invalid format.";
                            break;
                        case "committed-by":
                            if (value.Tag == LoreMetadataType.STRING) entry.CommittedBy = value.String;
                            else error = "The revision committer has an invalid format.";
                            break;
                        case "timestamp":
                            if (value.Tag != LoreMetadataType.NUMERIC || value.Numeric > 253402300799999UL)
                                error = "The revision timestamp has an invalid format.";
                            else if (value.Numeric != 0)
                                entry.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)value.Numeric);
                            break;
                    }
                }
            }
        }).WaitAsync();
        operation.Dispose();

        if (error is not null) throw new InvalidDataException(error);
        if (entries.Count > MaximumEntries) throw new InvalidDataException("Revision history exceeds the requested limit.");
        var history = entries.Select(entry => new RevisionHistoryEntry(entry.Number, entry.Revision, entry.Message,
            entry.CreatedBy ?? entry.CommittedBy, entry.Timestamp)).ToArray();
        return await authorResolver.ResolveAsync(path, history);
    }

    public sealed class RevisionHistoryAuthorResolver(IUserNameService userNames)
    {
        public async Task<IReadOnlyList<RevisionHistoryEntry>> ResolveAsync(string path, IReadOnlyList<RevisionHistoryEntry> entries)
        {
            var ids = entries.Select(entry => entry.Author)
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Select(author => author!)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) return entries;

            var result = await userNames.ResolveAsync(path, ids);
            return entries.Select(entry =>
            {
                if (entry.Author is null || !ids.Contains(entry.Author, StringComparer.Ordinal)) return entry;
                if (result.Names.TryGetValue(entry.Author, out var name) && !string.IsNullOrWhiteSpace(name)
                    && !string.Equals(name, entry.Author, StringComparison.Ordinal))
                    return entry with { AuthorName = name, AuthorResolutionWarning = result.Warning };
                return entry with { AuthorResolutionWarning = result.Warning ?? "Some authors could not be resolved." };
            }).ToArray();
        }
    }

    private sealed class EntryBuilder(ulong number, string revision)
    {
        public ulong Number { get; } = number;
        public string Revision { get; } = revision;
        public string? Message { get; set; }
        public string? CreatedBy { get; set; }
        public string? CommittedBy { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
