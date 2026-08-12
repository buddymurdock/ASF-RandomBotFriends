using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomBotFriends;

#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
internal sealed class RandomBotFriends : IASF, IGitHubPluginUpdates {
	private const byte DefaultMinFriends = 2;
	private const byte DefaultMaxFriends = 5;
	private const ushort DefaultDelayBetweenInvitesInSeconds = 60;

	// Random per-bot target friend count, picked once between MinFriends and MaxFriends and reused for the lifetime of the process
	private readonly ConcurrentDictionary<string, int> BotFriendTargets = new(StringComparer.Ordinal);

	private CancellationTokenSource? BackgroundLoopCts;
	private volatile bool CapacityWarningLogged;
	private ushort DelayBetweenInvitesInSeconds = DefaultDelayBetweenInvitesInSeconds;
	private bool Enabled;
	private byte MaxFriends = DefaultMaxFriends;
	private byte MinFriends = DefaultMinFriends;

	public string Name => nameof(RandomBotFriends);
	public string RepositoryName => "buddymurdock/ASF-RandomBotFriends";
	public Version Version => typeof(RandomBotFriends).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomBotFriendsEnabled / RandomBotFriendsMinFriends / RandomBotFriendsMaxFriends / RandomBotFriendsDelayBetweenInvites from the global ASF.json config
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
				}
			}
		}

		if (MinFriends > MaxFriends) {
			(MinFriends, MaxFriends) = (MaxFriends, MinFriends);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomBotFriends)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, will keep every bot's friend count between {MinFriends} and {MaxFriends}, with {DelayBetweenInvitesInSeconds}s between invites.");

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

	// Sends at most one friend invite per call, from a random bot that still needs friends towards a random other bot from this ASF instance that it's not already interacting with
	private async Task TrySendSingleInviteAsync() {
		IReadOnlyDictionary<string, Bot>? bots = Bot.BotsReadOnly;

		if ((bots == null) || (bots.Count < 2)) {
			return;
		}

		if (!CapacityWarningLogged && (MinFriends > bots.Count - 1)) {
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

			Bot? candidate = onlineBots.FirstOrDefault(otherBot => (otherBot != bot) && (otherBot.SteamID != 0) && (bot.SteamFriends.GetFriendRelationship(otherBot.SteamID) == EFriendRelationship.None));

			if (candidate == null) {
				continue;
			}

			bool success = await bot.ArchiHandler.AddFriend(candidate.SteamID).ConfigureAwait(false);

			if (success) {
				bot.ArchiLogger.LogGenericInfo($"Sent a random friend invite to {candidate.BotName} ({currentFriends + 1}/{target}).");
			} else {
				bot.ArchiLogger.LogGenericWarning($"Failed to send a friend invite to {candidate.BotName}.");
			}

			return;
		}
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
}
#pragma warning restore CA1812 // ASF uses this class during runtime
