# ASF-RandomBotFriends

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который периодически рассылает случайные заявки в друзья между ботами, запущенными в рамках этого же экземпляра ASF (bot-to-bot, друг другу).

Каждому боту при первом запуске назначается случайная цель по количеству друзей в диапазоне `[MinFriends; MaxFriends]`. Пока текущее количество друзей у бота меньше цели, плагин раз в `DelayBetweenInvites` секунд выбирает случайного бота, которому ещё не отправлена заявка/который ещё не в друзьях, и отправляет ему приглашение в друзья от имени другого бота. За один тик отправляется не более одной заявки на весь инстанс — это и есть задержка между инвайтами.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomBotFriendsEnabled": true,
	"RandomBotFriendsMinFriends": 20,
	"RandomBotFriendsMaxFriends": 50,
	"RandomBotFriendsDelayBetweenInvites": 60
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomBotFriendsEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomBotFriendsMinFriends` | `byte` (0-255) | `2` | Нижняя граница случайной цели по числу друзей для каждого бота. |
| `RandomBotFriendsMaxFriends` | `byte` (0-255) | `5` | Верхняя граница случайной цели по числу друзей для каждого бота. |
| `RandomBotFriendsDelayBetweenInvites` | `ushort`, секунды | `60` | Пауза между отправкой заявок в друзья (одна заявка за тик на весь инстанс ASF). |

Если `MinFriends` больше `MaxFriends`, значения меняются местами автоматически. Заявки отправляются только между ботами, залогиненными в Steam в момент проверки, и только тем, с кем ещё нет никаких отношений (не друг, заявка не отправлена/не получена, не заблокирован). Цель по числу друзей физически ограничена количеством остальных ботов в этом же ASF — если `MinFriends` больше, чем `количество ботов − 1`, плагин один раз выведет предупреждение в лог.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomBotFriends.git
cd ASF-RandomBotFriends
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).
