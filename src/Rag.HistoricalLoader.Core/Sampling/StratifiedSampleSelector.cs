using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Sampling;

public sealed class StratifiedSampleSelector
{
    public const string AlgorithmVersion = "1";
    public const string DefaultPrngAlgorithm = "splitmix64";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SampleSelectionResult Select(SampleSelectionRequest request, IReadOnlyList<Candidate> eligibleCandidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eligibleCandidates);

        if (request.ManifestState != ManifestState.Complete)
        {
            throw new InvalidOperationException($"Manifest '{request.ManifestId}' is not complete; sample selection is rejected.");
        }

        if (request.Budget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Budget must be positive.");
        }

        if (request.MinimumPerStratum < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Minimum per stratum must be at least one.");
        }

        var eligible = eligibleCandidates
            .Where(c => c.EligibilityCode == EligibilityCodes.Eligible)
            .OrderBy(c => c.Id)
            .ToList();

        var (bandBySize, sizeBands) = AssignSizeBands(eligible, request.SizeBandCount);

        var strata = eligible
            .GroupBy(c => StratumKey(c.Extension, bandBySize[c.ByteSize]), StringComparer.Ordinal)
            .Select(group => new Stratum(group.Key, FormatOf(group.Key), BandLabelOf(group.Key), group.OrderBy(c => c.Id).ToList()))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var limitations = BuildLimitations(request, eligible, sizeBands, strata);

        if (request.Budget < strata.Count * request.MinimumPerStratum)
        {
            var uncovered = strata.Select(s => new StratumCoverage(
                s.Key, s.Format, s.SizeBand, s.Candidates.Count, 0, 0,
                Covered: false, UncoveredReason: "budget_insufficient")).ToList();
            var failedSet = BuildSampleSet(request, uncovered, gatePassed: false, representative: false, limitations);
            return new SampleSelectionResult(false, "Budget cannot cover every observed stratum.", false, failedSet, [], uncovered, sizeBands, limitations);
        }

        var allocations = Allocate(strata, request.Budget, request.MinimumPerStratum);
        var prng = new SplitMix64(request.Seed);
        var members = new List<SampleMember>();
        var sampleSetId = DeriveSampleSetId(request);
        var rank = 0;
        foreach (var stratum in strata)
        {
            foreach (var candidate in SelectWithinStratum(stratum.Candidates, allocations[stratum.Key], prng))
            {
                members.Add(new SampleMember(sampleSetId, candidate.Id, stratum.Key, ComputeMetadataFingerprint(candidate), rank++));
            }
        }

        var coverage = strata.Select(s =>
        {
            var allocation = allocations[s.Key];
            return new StratumCoverage(
                s.Key, s.Format, s.SizeBand, s.Candidates.Count, allocation,
                (double)allocation / request.Budget, Covered: allocation >= request.MinimumPerStratum, UncoveredReason: null);
        }).ToList();

        var representative = request.MinimumPerStratum >= 1 && !string.IsNullOrWhiteSpace(request.ConfidenceCoverageRules);
        var sampleSet = BuildSampleSet(request, coverage, gatePassed: true, representative, limitations);
        return new SampleSelectionResult(true, null, representative, sampleSet, members, coverage, sizeBands, limitations);
    }

    public static string ComputeMetadataFingerprint(Candidate candidate)
    {
        var canonical = string.Join('|',
            candidate.Id.ToString("N"),
            candidate.RootId.ToString("N"),
            candidate.RelativePath,
            candidate.Extension,
            candidate.ByteSize,
            candidate.LastWriteTime.UtcTicks,
            candidate.EligibilityCode);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static (Dictionary<long, int> BandBySize, IReadOnlyList<SizeBandRange> Ranges) AssignSizeBands(
        IReadOnlyList<Candidate> eligible, int requestedBandCount)
    {
        var distinctSizes = eligible.Select(c => c.ByteSize).Distinct().OrderBy(size => size).ToList();
        var bandCount = Math.Max(1, Math.Min(requestedBandCount, distinctSizes.Count));
        var bandBySize = new Dictionary<long, int>();
        for (var i = 0; i < distinctSizes.Count; i++)
        {
            bandBySize[distinctSizes[i]] = Math.Min(bandCount - 1, i * bandCount / distinctSizes.Count) + 1;
        }

        var ranges = new List<SizeBandRange>();
        for (var band = 1; band <= bandCount; band++)
        {
            var sizes = distinctSizes.Where(size => bandBySize[size] == band).ToList();
            if (sizes.Count == 0)
            {
                continue;
            }

            ranges.Add(new SizeBandRange(band, sizes.Min(), sizes.Max(), eligible.Count(c => bandBySize[c.ByteSize] == band)));
        }

        return (bandBySize, ranges);
    }

    private static Dictionary<string, int> Allocate(IReadOnlyList<Stratum> strata, int budget, int minimumPerStratum)
    {
        var allocations = strata.ToDictionary(s => s.Key, _ => 0, StringComparer.Ordinal);
        foreach (var stratum in strata)
        {
            allocations[stratum.Key] = Math.Min(minimumPerStratum, stratum.Candidates.Count);
        }

        var remaining = budget - allocations.Values.Sum();
        if (remaining <= 0)
        {
            return allocations;
        }

        var totalPopulation = strata.Sum(s => s.Candidates.Count);
        var quotas = strata.Select(s =>
        {
            var quota = (double)remaining * s.Candidates.Count / totalPopulation;
            var floor = Math.Min((int)Math.Floor(quota), s.Candidates.Count - allocations[s.Key]);
            return (Key: s.Key, Quota: quota, Floor: floor);
        }).ToList();

        foreach (var quota in quotas)
        {
            allocations[quota.Key] += quota.Floor;
        }

        remaining -= quotas.Sum(q => q.Floor);

        var order = quotas
            .OrderByDescending(q => q.Quota - Math.Floor(q.Quota))
            .ThenBy(q => q.Key, StringComparer.Ordinal)
            .Select(q => q.Key)
            .ToList();

        while (remaining > 0)
        {
            var advanced = false;
            foreach (var key in order)
            {
                var stratum = strata.Single(s => s.Key == key);
                if (allocations[key] < stratum.Candidates.Count)
                {
                    allocations[key]++;
                    remaining--;
                    advanced = true;
                    break;
                }
            }

            if (!advanced)
            {
                break;
            }
        }

        return allocations;
    }

    private static IReadOnlyList<Candidate> SelectWithinStratum(IReadOnlyList<Candidate> candidates, int count, SplitMix64 prng)
    {
        var list = candidates.ToList();
        for (var i = 0; i < count && i < list.Count; i++)
        {
            var j = i + prng.NextInt32(list.Count - i);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list.Take(count).ToList();
    }

    private static IReadOnlyList<string> BuildLimitations(
        SampleSelectionRequest request,
        IReadOnlyList<Candidate> eligible,
        IReadOnlyList<SizeBandRange> sizeBands,
        IReadOnlyList<Stratum> strata)
    {
        var limitations = new List<string>
        {
            "Selection is limited to observed format and empirical size-band dimensions; it does not imply coverage of hidden document complexity or a guaranteed full-corpus duration.",
        };

        if (string.IsNullOrWhiteSpace(request.ConfidenceCoverageRules))
        {
            limitations.Add("Operator confidence/coverage rules were not supplied; the selection is not representative.");
        }

        var distinctSizes = eligible.Select(c => c.ByteSize).Distinct().Count();
        if (distinctSizes < request.SizeBandCount)
        {
            limitations.Add($"Requested {request.SizeBandCount} size bands but only {distinctSizes} distinct size(s) observed.");
        }

        foreach (var stratum in strata.Where(s => s.Candidates.Count == 1))
        {
            limitations.Add($"Outlier stratum '{stratum.Key}' has population 1.");
        }

        return limitations;
    }

    private static SampleSet BuildSampleSet(
        SampleSelectionRequest request,
        IReadOnlyList<StratumCoverage> coverage,
        bool gatePassed,
        bool representative,
        IReadOnlyList<string> limitations)
    {
        var allocations = coverage
            .Select(c => new SampleAllocation(c.StratumKey, c.Format, c.SizeBand, c.Population, c.Allocation, c.Weight))
            .ToList();
        return new SampleSet(
            DeriveSampleSetId(request),
            request.ManifestId,
            AlgorithmVersion,
            request.PrngAlgorithm,
            request.Seed,
            request.Budget,
            request.MinimumPerStratum,
            request.ConfidenceCoverageRules,
            gatePassed,
            representative,
            DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(new { allocations, limitations }, JsonOptions));
    }

    private static Guid DeriveSampleSetId(SampleSelectionRequest request)
    {
        var canonical = $"{request.ManifestId:N}|{request.Seed}|{request.Budget}|{request.MinimumPerStratum}|{request.SizeBandCount}|{request.ConfidenceCoverageRules}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..16]);
    }

    private static string StratumKey(string format, int band) => $"{format}|{band}";

    private static string FormatOf(string key) => key[..key.IndexOf('|')];

    private static string BandLabelOf(string key) => $"band_{key[(key.IndexOf('|') + 1)..]}";

    private sealed record Stratum(string Key, string Format, string SizeBand, List<Candidate> Candidates);

    internal sealed class SplitMix64
    {
        private ulong _state;

        public SplitMix64(long seed) => _state = unchecked((ulong)seed);

        public ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int NextInt32(int bound)
        {
            if (bound <= 1)
            {
                return 0;
            }

            var threshold = (uint)(-bound) % (uint)bound;
            while (true)
            {
                var value = (uint)NextUInt64();
                if (value >= threshold)
                {
                    return (int)(value % (uint)bound);
                }
            }
        }
    }
}
