using System.Collections.Immutable;

namespace Urlog.Core.Sources;

/// <summary>
/// The sources a process knows: the generated <c>LogSourceRegistry.Registrations</c> of each loaded assembly,
/// looked up by id or alias (case-insensitive). Ids and aliases are unique across the catalog.
/// </summary>
public sealed class SourceCatalog
{
    private readonly Dictionary<string, SourceRegistration> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceRegistration> _all = [];

    public IReadOnlyList<SourceRegistration> All => _all;

    public void Add(ImmutableArray<SourceRegistration> registrations)
    {
        foreach (var r in registrations)
        {
            foreach (var name in new[] { r.Id }.Concat(r.Aliases))
            {
                if (_byName.TryGetValue(name, out var existing) && !ReferenceEquals(existing, r))
                {
                    throw new InvalidOperationException($"log source name '{name}' is registered twice ('{existing.Id}' and '{r.Id}')");
                }
            }

            _byName[r.Id] = r;
            foreach (var alias in r.Aliases)
            {
                _byName[alias] = r;
            }

            _all.Add(r);
        }
    }

    public bool TryGet(string idOrAlias, out SourceRegistration registration) => _byName.TryGetValue(idOrAlias, out registration!);

    /// <summary>A fresh instance of the source called <paramref name="idOrAlias"/>.</summary>
    public ILogSource Create(string idOrAlias) =>
        TryGet(idOrAlias, out var r) ? r.Factory() : throw new KeyNotFoundException($"no log source called '{idOrAlias}'");
}
