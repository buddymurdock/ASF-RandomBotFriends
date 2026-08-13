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
	private const byte DefaultMinFriends = 2;
	private const byte DefaultMaxFriends = 5;
	private const ushort DefaultDelayBetweenInvitesInSeconds = 60;
	private const byte MaxCommentsToScan = 100;

	// Random per-bot target friend count, picked once between MinFriends and MaxFriends and reused for the lifetime of the process
	private readonly ConcurrentDictionary<string, int> BotFriendTargets = new(StringComparer.Ordinal);

	private CancellationTokenSource? BackgroundLoopCts;
	private volatile bool CapacityWarningLogged;
	private byte CommentsToScan = DefaultCommentsToScan;
	private ushort DelayBetweenInvitesInSeconds = DefaultDelayBetweenInvitesInSeconds;
	private bool Enabled;
	private bool InviteGroupCommenters;
	private bool InviteOwnBots = true;
	private byte MaxFriends = DefaultMaxFriends;
	private byte MinFriends = DefaultMinFriends;

	public string Name => nameof(RandomBotFriends);
	public string RepositoryName => "buddymurdock/ASF-RandomBotFriends";
	public Version Version => typeof(RandomBotFriends).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomBotFriendsEnabled / RandomBotFriendsMinFriends / RandomBotFriendsMaxFriends / RandomBotFriendsDelayBetweenInvites / RandomBotFriendsInviteOwnBots / RandomBotFriendsInviteGroupCommenters / RandomBotFriendsCommentsToScan from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomBotFriends)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomBotFriends)}MinFriends" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte minFriends):
						MinFriends = minFriends;

						break;
					case $"{nameof(RandomBotFriends)}MaxFriends" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte maxFriends):
						MaxFriends = maxFriends;

						break;
					case $"{nameof(RandomBotFriends)}DelayBetweenInvites" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort delayBetweenInvites) && (delayBetweenInvites > 0):
						DelayBetweenInvitesInSeconds = delayBetweenInvites;

						break;
					case $"{nameof(RandomBotFriends)}InviteOwnBots" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						InviteOwnBots = configValue.GetBoolean();

						break;
					case $"{nameof(RandomBotFriends)}InviteGroupCommenters" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						InviteGroupCommenters = configValue.GetBoolean();

						break;
					case $"{nameof(RandomBotFriends)}CommentsToScan" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte commentsToScan) && (commentsToScan > 0):
						CommentsToScan = commentsToScan;

						break;
				}
			}
		}

		if (MinFriends > MaxFriends) {
			(MinFriends, MaxFriends) = (MaxFriends, MinFriends);
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

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, will keep every bot's friend count between {MinFriends} and {MaxFriends}, with {DelayBetweenInvitesInSeconds}s between invites. Sources: {(InviteOwnBots ? "own bots" : null)}{((InviteOwnBots && InviteGroupCommenters) ? " + " : null)}{(InviteGroupCommenters ? $"commenters from own groups (last {CommentsToScan})" : null)}.");

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

	private async Task BackgroundLoopAsync(CancellationToken cancellationToken) {
		using PeriodicTimer timer = new(TimeSpan.FromSeconds(DelayBetweenInvitesInSeconds));

		while (!cancellationToken.IsCancellationRequested) {
			bool shouldContinue;

			try {
				shouldContinue = await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (!shouldContinue) {
				break;
			}

			try {
				await TrySendSingleInviteAsync().ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Sends at most one friend invite per call, from a random bot that still needs friends towards a random candidate it's not already interacting with
	private async Task TrySendSingleInviteAsync() {
		IReadOnlyDictionary<string, Bot>? bots = Bot.BotsReadOnly;

		if ((bots == null) || (bots.Count == 0)) {
			return;
		}

		if (InviteOwnBots && !CapacityWarningLogged && (MinFriends > bots.Count - 1)) {
			CapacityWarningLogged = true;

			ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomBotFriends)}MinFriends ({MinFriends}) is higher than the number of other bots available in this ASF instance ({bots.Count - 1}); some bots may never reach their target.");
		}

		List<Bot> onlineBots = [.. bots.Values.Where(static bot => bot.IsConnectedAndLoggedOn).OrderBy(static _ => Random.Shared.Next())];

		foreach (Bot bot in onlineBots) {
			int target = BotFriendTargets.GetOrAdd(bot.BotName, _ => MinFriends == MaxFriends ? MinFriends : Random.Shared.Next(MinFriends, MaxFriends + 1));

			int currentFriends = GetActualFriendCount(bot);

			if (currentFriends >= target) {
				continue;
			}

			(ulong CandidateSteamID, string SourceLabel)? candidate = await GetCandidateAsync(bot, bots, onlineBots).ConfigureAwait(false);

			if (candidate == null) {
				continue;
			}

			bool success = await bot.ArchiHandler.AddFriend(candidate.Value.CandidateSteamID).ConfigureAwait(false);

			if (success) {
				bot.ArchiLogger.LogGenericInfo($"Sent a random friend invite to {candidate.Value.CandidateSteamID} ({candidate.Value.SourceLabel}) ({currentFriends + 1}/{target}).");
			} else {
				bot.ArchiLogger.LogGenericWarning($"Failed to send a friend invite to {candidate.Value.CandidateSteamID} ({candidate.Value.SourceLabel}).");
			}

			return;
		}
	}

	// Tries whichever candidate sources are enabled, in random order, and returns the first hit
	private async Task<(ulong SteamID, string SourceLabel)?> GetCandidateAsync(Bot bot, IReadOnlyDictionary<string, Bot> bots, List<Bot> onlineBots) {
		// true = try the own-bots source, false = try the group-commenters source; these are fixed source tags, shuffled independently of which sources are actually enabled (checked below)
		List<bool> sources = [.. new[] { true, false }.OrderBy(static _ => Random.Shared.Next())];

		foreach (bool isOwnBotsSource in sources) {
			if (isOwnBotsSource) {
				if (!InviteOwnBots) {
					continue;
				}

				Bot? candidate = PickOwnBotCandidate(bot, onlineBots);

				if (candidate != null) {
					return (candidate.SteamID, "own bot");
				}
			} else {
				if (!InviteGroupCommenters) {
					continue;
				}

				ulong? candidateSteamID = await GetGroupCommenterCandidateAsync(bot, bots).ConfigureAwait(false);

				if (candidateSteamID != null) {
					return (candidateSteamID.Value, "group commenter");
				}
			}
		}

		return null;
	}

	private static Bot? PickOwnBotCandidate(Bot bot, List<Bot> onlineBots) => onlineBots.FirstOrDefault(otherBot => (otherBot != bot) && (otherBot.SteamID != 0) && (bot.SteamFriends.GetFriendRelationship(otherBot.SteamID) == EFriendRelationship.None));

	// Picks a random group the bot is already a member of, pulls its most recent CommentsToScan wall comments, and returns a random commenter the bot isn't already related to
	private async Task<ulong?> GetGroupCommenterCandidateAsync(Bot bot, IReadOnlyDictionary<string, Bot> bots) {
		List<ulong> ownGroups = [];
		int clanCount = bot.SteamFriends.GetClanCount();

		for (int i = 0; i < clanCount; i++) {
			SteamID clanID = bot.SteamFriends.GetClanByIndex(i);

			if (bot.SteamFriends.GetClanRelationship(clanID) == EClanRelationship.Member) {
				ownGroups.Add(clanID.ConvertToUInt64());
			}
		}

		if (ownGroups.Count == 0) {
			return null;
		}

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

		List<ulong> candidates = [
			.. commenterSteamIDs.Where(
				steamID => (steamID != bot.SteamID) &&
					!bots.Values.Any(otherBot => otherBot.SteamID == steamID) &&
					(bot.SteamFriends.GetFriendRelationship(new SteamID(steamID)) == EFriendRelationship.None)
			)
		];

		return candidates.Count > 0 ? candidates[Random.Shared.Next(candidates.Count)] : null;
	}

	// SteamFriends.GetFriendCount() returns the size of the whole friend-list cache (pending, blocked, ignored, etc, not just accepted friends), so we need to filter it down ourselves
	private static int GetActualFriendCount(Bot bot) {
		int cacheSize = bot.SteamFriends.GetFriendCount();
		int friends = 0;

		for (int i = 0; i < cacheSize; i++) {
			SteamID steamID = bot.SteamFriends.GetFriendByIndex(i);

			if (bot.SteamFriends.GetFriendRelationship(steamID) == EFriendRelationship.Friend) {
				friends++;
			}
		}

		return friends;
	}

	[GeneratedRegex("data-miniprofile=\"(\\d+)\"")]
	private static partial Regex CommenterAccountIDRegex();

	private sealed record CommentsRenderResponse([property: JsonPropertyName("success")] bool Success, [property: JsonPropertyName("comments_html")] string? CommentsHtml);
}
#pragma warning restore CA5394 // Randomness here only picks arbitrary friend targets/order, it's not used for anything security-sensitive
#pragma warning restore CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning restore CA1812 // ASF uses this class during runtime
