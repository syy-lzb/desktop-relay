using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DesktopRelay.App;
public static class AutoProxy
{
    public static async Task<List<LocalProxyCandidate>> ResolveAsync(string? system, Func<Task<List<LocalProxyCandidate>>> discover, Func<string,Task<bool>> probe)
    {
        if (system != null && await probe(system)) return new() { new(system,"系统代理") };
        var candidates = (await discover()).Where(c => c.Address != system).DistinctBy(c => c.Address).ToList();
        using var slots = new System.Threading.SemaphoreSlim(4);
        var checkedCandidates = await Task.WhenAll(candidates.Select(async candidate => {
            await slots.WaitAsync();
            try { return (candidate, valid: await probe(candidate.Address)); }
            finally { slots.Release(); }
        }));
        return checkedCandidates.Where(c => c.valid).Select(c => c.candidate).ToList();
    }
}
