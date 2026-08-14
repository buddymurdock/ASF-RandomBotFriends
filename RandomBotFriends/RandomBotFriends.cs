using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomBotFriends;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks arbitrary friend targets/order, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed partial class RandomBotFriends : IASF, IGitHubPluginUpdates {
	// SteamID64 = this + the 32-bit account ID Steam embeds in every comment's data-miniprofile attribute
	private const ulong IndividualAccountIDBase = 76561197960265728UL;

	private const byte DefaultCommentsToScan = 50;
	private const ushort DefaultMaxDelayBetweenInvitesInSeconds = 90;
	private const ushort DefaultMinDelayBetweenInvitesInSeconds = 30;
	private const byte DefaultGroupCommentersMaxFriends = 3;
	private const byte DefaultGroupCommentersMinFriends = 1;
	private const byte DefaultGroupCommentersMaxScanIntervalHours = 28;
	private const byte DefaultGroupCommentersMinScanIntervalHours = 20;
	private const byte DefaultOwnBotsMaxFriends = 5;
	private const byte DefaultOwnBotsMinFriends = 2;
	private const byte MaxCommentsToScan = 100;

	// Random per-bot targets, picked once and reused for the lifetime of the process. Kept separate so each source's volume can be capped independently
	private readonly ConcurrentDictionary<string, int> BotGroupCommentersTargets = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> BotOwnBotsTargets = new(StringComparer.Ordinal);

	// Cached commenter pool per bot, refreshed only once every [GroupCommentersMinScanIntervalHours; GroupCommentersMaxScanIntervalHours]
	// (re-rolled per bot after every scan) instead of on every invite tick - fetching the same group's comment wall every 30-90s forever
	// would itself be a machine-detectable pattern, independent of anything about the friend invites it feeds
	private readonly ConcurrentDictionary<string, List<ulong>> BotGroupCommentersCache = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, DateTime> BotGroupCommentersNextScanAt = new(StringComparer.Ordinal);

	private CancellationTokenSource? BackgroundLoopCts;
	private volatile bool CapacityWarningLogged;
	private byte CommentsToScan = DefaultCommentsToScan;
	private bool Enabled;
	private byte GroupCommentersMaxFriends = DefaultGroupCommentersMaxFriends;
	private byte GroupCommentersMaxScanIntervalHours = DefaultGroupCommentersMaxScanIntervalHours;
	private byte GroupCommentersMinFriends = DefaultGroupCommentersMinFriends;
	private byte GroupCommentersMinScanIntervalHours = DefaultGroupCommentersMinScanIntervalHours;

	// At most one steamcommunity.com fetch per tick, regardless of how many bots' caches expired this tick; reset at the start of every tick.
	// With per-bot caching this mostly matters when several bots' caches happen to expire in the same tick - spreads their rescans out instead of bursting
	private bool GroupCommentersAttemptedThisTick;

	private bool InviteGroupCommenters;
	private bool InviteOwnBots = true;
	private ushort MaxDelayBetweenInvitesInSeconds = DefaultMaxDelayBetweenInvitesInSeconds;
	private ushort MinDelayBetweenInvitesInSeconds = DefaultMinDelayBetweenInvitesInSeconds;
	private byte OwnBotsMaxFriends = DefaultOwnBotsMaxFriends;
	private byte OwnBotsMinFriends = DefaultOwnBotsMinFriends;

	// Extra groups to scan for commenters regardless of whether the bot is actually a member - e.g. dedicated
	// "looking for friends" groups. Merged with the bot's own group memberships, not a replacement for them
	private ulong[] GroupCommentersTargetGroupIDs = [];

	public string Name => nameof(RandomBotFriends);
	public string RepositoryName => "buddymurdock/ASF-RandomBotFriends";
	public Version Version => typeof(RandomBotFriends).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomBotFriendsEnabled / RandomBotFriendsMinDelayBetweenInvites / RandomBotFriendsMaxDelayBetweenInvites / RandomBotFriendsInviteOwnBots / RandomBotFriendsOwnBotsMinFriends /
	// RandomBotFriendsOwnBotsMaxFriends / RandomBotFriendsInviteGroupCommenters / RandomBotFriendsGroupCommentersMinFriends / RandomBotFriendsGroupCommentersMaxFriends / RandomBotFriendsCommentsToScan /
	// RandomBotFriendsGroupCommentersTargetGroupIDs / RandomBotFriendsGroupCommentersMinScanIntervalHours / RandomBotFriendsGroupCommentersMaxScanIntervalHours from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		HashSet<ulong> parsedTargetGroupIDs = [];

		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomBotFriends)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomBotFriends)}MinDelayBetweenInvites" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelayBetweenInvites) && (minDelayBetweenInvites > 0):
						MinDelayBetweenInvitesInSeconds = minDelayBetweenInvites;

						break;
					case $"{nameof(RandomBotFriends)}MaxDelayBetweenInvites" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelayBetweenInvites) && (maxDelayBetweenInvites > 0):
						MaxDelayBetweenInvitesInSeconds = maxDelayBetweenInvites;

						break;
					case $"{nameof(RandomBotFriends)}InviteOwnBots" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						InviteOwnBots = configValue.GetBoolean();

						break;
					case $"{nameof(RandomBotFriends)}OwnBotsMinFriends" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte ownBotsMinFriends):
						OwnBotsMinFriends = ownBotsMinFriends;

						break;
					case $"{nameof(RandomBotFriends)}OwnBotsMaxFriends" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte ownBotsMaxFriends):
						OwnBotsMaxFriends = ownBotsMaxFriends;

						break;
					case $"{nameof(RandomBotFriends)}InviteGroupCommenters" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						InviteGroupCommenters = configValue.GetBoolean();

						break;
					case $"{nameof(RandomBotFriends)}GroupCommentersMinFriends" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte groupCommentersMinFriends):
						GroupCommentersMinFriends = groupCommentersMinFriends;

						break;
					case $"{nameof(RandomBotFriends)}GroupCommentersMaxFriends" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte groupCommentersMaxFriends):
						GroupCommentersMaxFriends = groupCommentersMaxFriends;

						break;
					case $"{nameof(RandomBotFriends)}CommentsToScan" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte commentsToScan) && (commentsToScan > 0):
						CommentsToScan = commentsToScan;

						break;
					case $"{nameof(RandomBotFriends)}GroupCommentersTargetGroupIDs" when configValue.ValueKind == JsonValueKind.Array:
						AddParsedGroupIDs(configValue, parsedTargetGroupIDs);

						break;
					case $"{nameof(RandomBotFriends)}GroupCommentersMinScanIntervalHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte groupCommentersMinScanIntervalHours) && (groupCommentersMinScanIntervalHours > 0):
						GroupCommentersMinScanIntervalHours = groupCommentersMinScanIntervalHours;

						break;
					case $"{nameof(RandomBotFriends)}GroupCommentersMaxScanIntervalHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte groupCommentersMaxScanIntervalHours) && (groupCommentersMaxScanIntervalHours > 0):
						GroupCommentersMaxScanIntervalHours = groupCommentersMaxScanIntervalHours;

						break;
				}
			}
		}

		GroupCommentersTargetGroupIDs = [.. parsedTargetGroupIDs];

		if (MinDelayBetweenInvitesInSeconds > MaxDelayBetweenInvitesInSeconds) {
			(MinDelayBetweenInvitesInSeconds, MaxDelayBetweenInvitesInSeconds) = (MaxDelayBetweenInvitesInSeconds, MinDelayBetweenInvitesInSeconds);
		}

		if (GroupCommentersMinScanIntervalHours > GroupCommentersMaxScanIntervalHours) {
			(GroupCommentersMinScanIntervalHours, GroupCommentersMaxScanIntervalHours) = (GroupCommentersMaxScanIntervalHours, GroupCommentersMinScanIntervalHours);
		}

		if (OwnBotsMinFriends > OwnBotsMaxFriends) {
			(OwnBotsMinFriends, OwnBotsMaxFriends) = (OwnBotsMaxFriends, OwnBotsMinFriends);
		}

		if (GroupCommentersMinFriends > GroupCommentersMaxFriends) {
			(GroupCommentersMinFriends, GroupCommentersMaxFriends) = (GroupCommentersMaxFriends, GroupCommentersMinFriends);
		}

		if (CommentsToScan > MaxCommentsToScan) {
			CommentsToScan = MaxCommentsToScan;
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomBotFriends)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		if (!InviteOwnBots && !InviteGroupCommenters) {
			ASF.ArchiLogger.LogGenericWarning($"{Name} is enabled, but both {nameof(RandomBotFriends)}InviteOwnBots and {nameof(RandomBotFriends)}InviteGroupCommenters are false, so there's no candidate source to invite from.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, {MinDelayBetweenInvitesInSeconds}-{MaxDelayBetweenInvitesInSeconds}s between invites. Sources: {(InviteOwnBots ? $"own bots ({OwnBotsMinFriends}-{OwnBotsMaxFriends} friends/bot)" : null)}{((InviteOwnBots && InviteGroupCommenters) ? " + " : null)}{(InviteGroupCommenters ? $"group commenters ({GroupCommentersMinFriends}-{GroupCommentersMaxFriends} friends/bot, rescanned every {GroupCommentersMinScanIntervalHours}-{GroupCommentersMaxScanIntervalHours}h, last {CommentsToScan} comments, {GroupCommentersTargetGroupIDs.Length} target group(s) + own memberships)" : null)}.");

		if (BackgroundLoopCts != null) {
			// OnASFInit() should only ever be called once per process, this is just a safety net against a possible double start
			return Task.CompletedTask;
		}

		BackgroundLoopCts = new CancellationTokenSource();

		Utilities.InBackground(() => BackgroundLoopAsync(BackgroundLoopCts.Token), true);

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	// Delay is re-rolled every tick within [MinDelayBetweenInvitesInSeconds; MaxDelayBetweenInvitesInSeconds] instead of using a fixed-period PeriodicTimer -
	// a perfectly metronomic tick interval running around the clock is itself a machine-detectable pattern, independent of anything visible to other users
	private async Task BackgroundLoopAsync(CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			TimeSpan delay = GetRandomDelay(MinDelayBetweenInvitesInSeconds, MaxDelayBetweenInvitesInSeconds);

			try {
				await LongDelayAsync(delay, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			try {
				await TrySendSingleInviteAsync().ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Task.Delay's underlying timer caps out at ~49.7 days (uint.MaxValue-1 ms) - a delay past that
	// throws ArgumentOutOfRangeException synchronously, which would go unhandled here and crash the
	// entire ASF process via OnUnobservedTaskException (this exact bug hit RandomNickname/RandomProfileAvatar/
	// RandomProfileBackground in production). Chunking sidesteps the limit for arbitrarily long delays -
	// needed here now that GetRandomDelay below no longer guarantees an upper bound the way uniform did.
	// Only applied to the main invite tick - GroupCommentersMinScanIntervalHours/MaxScanIntervalHours below
	// feeds DateTime.AddHours() as a "next allowed scan" timestamp, not Task.Delay, so it isn't at risk here.
	private static async Task LongDelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
		TimeSpan chunk = TimeSpan.FromDays(1);

		while (delay > chunk) {
			await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
			delay -= chunk;
		}

		if (delay > TimeSpan.Zero) {
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	// Real people don't wait a uniformly random amount of time between actions - intervals tend
	// to cluster around a typical gap with occasional much shorter/longer ones (bursty/heavy-tailed),
	// not spread flat across [min, max]. Log-normal captures that: min/max become the ~5th/95th
	// percentiles rather than hard bounds, with sqrt(min*max) as the median.
	// z is clamped before use because extreme (min, max) ratios produce a large sigma - an un-clamped
	// Box-Muller tail can drive Math.Exp()/TimeSpan construction into Infinity/OverflowException, the
	// same failure class LongDelayAsync above was written to fix. The final Math.Clamp is a second,
	// independent safety net on the result itself, keeping delays (and LongDelayAsync's chunking loop)
	// bounded to something sane even for pathological configs.
	private static TimeSpan GetRandomDelay(ushort minSeconds, ushort maxSeconds) {
		if (minSeconds == maxSeconds) {
			return TimeSpan.FromSeconds(minSeconds);
		}

		double median = Math.Sqrt((double) minSeconds * maxSeconds);
		double sigma = Math.Log((double) maxSeconds / minSeconds) / (2 * 1.645);

		double u1 = 1.0 - Random.Shared.NextDouble();
		double u2 = Random.Shared.NextDouble();
		double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

		z = Math.Clamp(z, -3.5, 3.5);

		double seconds = median * Math.Exp(sigma * z);
		seconds = Math.Clamp(seconds, minSeconds / 10.0, maxSeconds * 5.0);

		return TimeSpan.FromSeconds(seconds);
	}

	// Sends at most one friend invite per call, from a random bot that still needs friends towards a random candidate it's not already interacting with
	private async Task TrySendSingleInviteAsync() {
		IReadOnlyDictionary<string, Bot>? bots = Bot.BotsReadOnly;

		if ((bots == null) || (bots.Count == 0)) {
			return;
		}

		if (InviteOwnBots && !CapacityWarningLogged && (OwnBotsMinFriends > bots.Count - 1)) {
			CapacityWarningLogged = true;

			ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomBotFriends)}OwnBotsMinFriends ({OwnBotsMinFriends}) is higher than the number of other bots available in this ASF instance ({bots.Count - 1}); some bots may never reach their target.");
		}

		List<Bot> onlineBots = [.. bots.Values.Where(static bot => bot.IsConnectedAndLoggedOn).OrderBy(static _ => Random.Shared.Next())];
		HashSet<ulong> ownBotSteamIDs = [.. bots.Values.Select(static otherBot => otherBot.SteamID).Where(static steamID => steamID != 0)];

		GroupCommentersAttemptedThisTick = false;

		foreach (Bot bot in onlineBots) {
			(ulong SteamID, string SourceLabel, int Occupied, int Target)? candidate;

			// Randomize which enabled source gets tried first, falling back to the other one if the first found nothing (or already met its own target)
			if (Random.Shared.Next(2) == 0) {
				candidate = TryOwnBotsCandidate(bot, onlineBots, ownBotSteamIDs);

				if (candidate == null) {
					candidate = await TryGroupCommentersCandidateAsync(bot, bots, ownBotSteamIDs).ConfigureAwait(false);
				}
			} else {
				candidate = await TryGroupCommentersCandidateAsync(bot, bots, ownBotSteamIDs).ConfigureAwait(false);

				if (candidate == null) {
					candidate = TryOwnBotsCandidate(bot, onlineBots, ownBotSteamIDs);
				}
			}

			if (candidate == null) {
				continue;
			}

			bool success = await bot.ArchiHandler.AddFriend(candidate.Value.SteamID).ConfigureAwait(false);

			if (success) {
				bot.ArchiLogger.LogGenericInfo($"Sent a random friend invite to {candidate.Value.SteamID} ({candidate.Value.SourceLabel}) ({candidate.Value.Occupied + 1}/{candidate.Value.Target}).");
			} else {
				bot.ArchiLogger.LogGenericWarning($"Failed to send a friend invite to {candidate.Value.SteamID} ({candidate.Value.SourceLabel}).");
			}

			return;
		}
	}

	private static Bot? PickOwnBotCandidate(Bot bot, List<Bot> onlineBots) => onlineBots.FirstOrDefault(otherBot => (otherBot != bot) && (otherBot.SteamID != 0) && (bot.SteamFriends.GetFriendRelationship(otherBot.SteamID) == EFriendRelationship.None));

	private (ulong SteamID, string SourceLabel, int Occupied, int Target)? TryOwnBotsCandidate(Bot bot, List<Bot> onlineBots, HashSet<ulong> ownBotSteamIDs) {
		if (!InviteOwnBots) {
			return null;
		}

		int target = BotOwnBotsTargets.GetOrAdd(bot.BotName, _ => OwnBotsMinFriends == OwnBotsMaxFriends ? OwnBotsMinFriends : Random.Shared.Next(OwnBotsMinFriends, OwnBotsMaxFriends + 1));
		int occupied = GetOccupiedFriendSlots(bot, ownBotSteamIDs, countOwnBots: true);

		if (occupied >= target) {
			return null;
		}

		Bot? ownBotCandidate = PickOwnBotCandidate(bot, onlineBots);

		return ownBotCandidate != null ? (ownBotCandidate.SteamID, "own bot", occupied, target) : null;
	}

	private async Task<(ulong SteamID, string SourceLabel, int Occupied, int Target)?> TryGroupCommentersCandidateAsync(Bot bot, IReadOnlyDictionary<string, Bot> bots, HashSet<ulong> ownBotSteamIDs) {
		if (!InviteGroupCommenters) {
			return null;
		}

		int target = BotGroupCommentersTargets.GetOrAdd(bot.BotName, _ => GroupCommentersMinFriends == GroupCommentersMaxFriends ? GroupCommentersMinFriends : Random.Shared.Next(GroupCommentersMinFriends, GroupCommentersMaxFriends + 1));
		int occupied = GetOccupiedFriendSlots(bot, ownBotSteamIDs, countOwnBots: false);

		if (occupied >= target) {
			return null;
		}

		ulong? commenterCandidate = await GetGroupCommenterCandidateAsync(bot, bots).ConfigureAwait(false);

		return commenterCandidate != null ? (commenterCandidate.Value, "group commenter", occupied, target) : null;
	}

	// The raw commenter pool is only (re)scanned once every [GroupCommentersMinScanIntervalHours; GroupCommentersMaxScanIntervalHours]
	// per bot - re-rolled after every scan, so it's neither a fixed period nor "once a tick" - and cached in between. Only an actual
	// cache miss counts against the once-per-tick fetch limit below; serving from an already-warm cache never touches steamcommunity.com
	private async Task<ulong?> GetGroupCommenterCandidateAsync(Bot bot, IReadOnlyDictionary<string, Bot> bots) {
		List<ulong>? cachedCommenterIDs = BotGroupCommentersCache.GetValueOrDefault(bot.BotName);
		bool cacheIsStale = !BotGroupCommentersNextScanAt.TryGetValue(bot.BotName, out DateTime nextScanAt) || (DateTime.UtcNow >= nextScanAt);

		if (cacheIsStale) {
			if (GroupCommentersAttemptedThisTick) {
				// Another bot already used this tick's single steamcommunity.com fetch budget - fall back to whatever we have cached (possibly nothing) and try rescanning next tick
				return PickLiveCandidate(bot, bots, cachedCommenterIDs);
			}

			GroupCommentersAttemptedThisTick = true;

			List<ulong>? freshCommenterIDs = await ScanGroupCommentersAsync(bot).ConfigureAwait(false);

			if (freshCommenterIDs != null) {
				cachedCommenterIDs = freshCommenterIDs;
				BotGroupCommentersCache[bot.BotName] = freshCommenterIDs;
			}

			int scanIntervalHours = GroupCommentersMinScanIntervalHours == GroupCommentersMaxScanIntervalHours ? GroupCommentersMinScanIntervalHours : Random.Shared.Next(GroupCommentersMinScanIntervalHours, GroupCommentersMaxScanIntervalHours + 1);
			BotGroupCommentersNextScanAt[bot.BotName] = DateTime.UtcNow.AddHours(scanIntervalHours);
		}

		return PickLiveCandidate(bot, bots, cachedCommenterIDs);
	}

	// Re-checks relationships live (accepted friends/invites change between scans) and picks a random still-eligible commenter
	private static ulong? PickLiveCandidate(Bot bot, IReadOnlyDictionary<string, Bot> bots, List<ulong>? commenterIDs) {
		if ((commenterIDs == null) || (commenterIDs.Count == 0)) {
			return null;
		}

		List<ulong> candidates = [
			.. commenterIDs.Where(
				steamID => (steamID != bot.SteamID) &&
					!bots.Values.Any(otherBot => otherBot.SteamID == steamID) &&
					(bot.SteamFriends.GetFriendRelationship(new SteamID(steamID)) == EFriendRelationship.None)
			)
		];

		return candidates.Count > 0 ? candidates[Random.Shared.Next(candidates.Count)] : null;
	}

	// Picks a random group - either one the bot is already a member of, or one of the configured
	// GroupCommentersTargetGroupIDs (those don't require membership, a public Steam group's comment wall
	// is readable by anyone) - and pulls its most recent CommentsToScan wall comments
	private async Task<List<ulong>?> ScanGroupCommentersAsync(Bot bot) {
		HashSet<ulong> candidateGroupsSet = [.. GroupCommentersTargetGroupIDs];
		int clanCount = bot.SteamFriends.GetClanCount();

		for (int i = 0; i < clanCount; i++) {
			SteamID clanID = bot.SteamFriends.GetClanByIndex(i);

			if (bot.SteamFriends.GetClanRelationship(clanID) == EClanRelationship.Member) {
				candidateGroupsSet.Add(clanID.ConvertToUInt64());
			}
		}

		if (candidateGroupsSet.Count == 0) {
			return null;
		}

		List<ulong> ownGroups = [.. candidateGroupsSet];
		ulong groupID = ownGroups[Random.Shared.Next(ownGroups.Count)];

		Uri request = new($"https://steamcommunity.com/comment/Clan/render/{groupID}/-1/?start=0&count={CommentsToScan}");

		ObjectResponse<CommentsRenderResponse>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<CommentsRenderResponse>(request).ConfigureAwait(false);

		string? html = response?.Content?.CommentsHtml;

		if (string.IsNullOrEmpty(html)) {
			return null;
		}

		HashSet<ulong> commenterSteamIDs = [];

		foreach (Match match in CommenterAccountIDRegex().Matches(html)) {
			if (uint.TryParse(match.Groups[1].ValueSpan, out uint accountID) && (accountID != 0)) {
				commenterSteamIDs.Add(IndividualAccountIDBase + accountID);
			}
		}

		return [.. commenterSteamIDs];
	}

	private static void AddParsedGroupIDs(JsonElement array, HashSet<ulong> target) {
		foreach (JsonElement groupElement in array.EnumerateArray()) {
			ulong? groupID = groupElement.ValueKind switch {
				JsonValueKind.Number when groupElement.TryGetUInt64(out ulong numericID) => numericID,
				JsonValueKind.String when ulong.TryParse(groupElement.GetString(), out ulong stringID) => stringID,
				_ => null
			};

			if ((groupID is { } validGroupID) && (validGroupID != 0) && new SteamID(validGroupID).IsClanAccount) {
				target.Add(validGroupID);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid {nameof(RandomBotFriends)}GroupCommentersTargetGroupIDs entry: {groupElement}.");
			}
		}
	}

	// SteamFriends.GetFriendCount() returns the size of the whole friend-list cache (pending, blocked, ignored, etc), so we filter it down to what actually occupies a target slot:
	// accepted friends plus our own outstanding invites (RequestInitiator), split by whether the other side is one of our own bots or not - so the own-bots and group-commenters
	// targets stay independent instead of sharing one combined friend count
	private static int GetOccupiedFriendSlots(Bot bot, HashSet<ulong> ownBotSteamIDs, bool countOwnBots) {
		int cacheSize = bot.SteamFriends.GetFriendCount();
		int occupiedSlots = 0;

		for (int i = 0; i < cacheSize; i++) {
			SteamID steamID = bot.SteamFriends.GetFriendByIndex(i);

			if (bot.SteamFriends.GetFriendRelationship(steamID) is not (EFriendRelationship.Friend or EFriendRelationship.RequestInitiator)) {
				continue;
			}

			bool isOwnBot = ownBotSteamIDs.Contains(steamID.ConvertToUInt64());

			if (isOwnBot == countOwnBots) {
				occupiedSlots++;
			}
		}

		return occupiedSlots;
	}

	[GeneratedRegex("data-miniprofile=\"(\\d+)\"")]
	private static partial Regex CommenterAccountIDRegex();

	private sealed record CommentsRenderResponse([property: JsonPropertyName("success")] bool Success, [property: JsonPropertyName("comments_html")] string? CommentsHtml);
}
#pragma warning restore CA5394 // Randomness here only picks arbitrary friend targets/order, it's not used for anything security-sensitive
#pragma warning restore CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning restore CA1812 // ASF uses this class during runtime
