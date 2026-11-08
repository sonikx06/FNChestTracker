# Chest Tracker — client guide / руководство

## English

**Install.** Unzip `Client/` anywhere and run `FortniteChestTracker.exe`, or `Start Chest Tracker.cmd`. You don't need
.NET or an installer. Requires Windows 10 1903+ or Windows 11, x64.

**Play solo.** Everything works offline. Default hotkeys:

| Key | Action |
|---|---|
| F8 | Chest +1 |
| F7 | Remove the last chest |
| F9 | Start match |
| F10 | End match |
| F6 | Show/hide the overlay |

Any action can be rebound in **Settings → Hotkeys** to another key or to the mouse side buttons **Mouse4 / Mouse5**:
click the field, then press the key or the mouse button. Mouse buttons keep working in the game as usual.
The dashboard has one **Start Match / End Match** button. Its main counters are the current match, **all-time chests**
and **total matches**.

**Play with friends:**
1. Open the **Server** page and enter the address your server owner gave you, for example `https://chests.example.com`.
2. Press **Connect**.
3. Choose how to sign in:
   - **Guest:** just a name, this PC only.
   - **Log in / Sign up:** username and password, usable from any PC. A guest can later be turned into an account
     without losing statistics.
4. Open the **Party** page. Create a party and share the `XXXX-XXXX` code, or join with a friend's code.
5. Shared matches start and end with the owner's F9/F10.

**Server page:**
- Status and ping.
- Connect and Disconnect.
- Auto-connect at start-up, with automatic reconnection.
- Saved servers.
- Clear error messages: unreachable, certificate problem, wrong password, version too old.

**Offline behaviour.** If the connection drops, your chests are queued on disk and sent automatically later.

**Privacy.**
- Only chest events (time, +1/−1, source) are shared.
- Your password is never stored; device keys are encrypted with Windows DPAPI.
- No Fortnite or Epic credentials are used.
- The app never reads or modifies the game.

## Русский

**Установка.** Распакуйте папку `Client/` и запустите `FortniteChestTracker.exe` или `Start Chest Tracker.cmd`.
Устанавливать .NET не нужно. Требуется Windows 10 1903+ или Windows 11, x64.

**Один.** Всё работает без интернета. Клавиши по умолчанию:

| Клавиша | Действие |
|---|---|
| F8 | Сундук +1 |
| F7 | Убрать последний сундук |
| F9 | Начать матч |
| F10 | Завершить матч |
| F6 | Показать/скрыть оверлей |

Любое действие можно переназначить в **«Настройки → Горячие клавиши»** на другую клавишу или на боковые кнопки мыши
**Mouse4 / Mouse5**: нажмите на поле, затем клавишу или кнопку мыши. В игре кнопки мыши продолжают работать как обычно.
На главной одна кнопка **«Начать матч / Завершить матч»**. Главные счётчики: текущий матч, **сундуки за всё время**
и **всего матчей**.

**С друзьями:**
1. Откройте страницу **«Сервер»** и введите адрес от владельца сервера, например `https://chests.example.com`.
2. Нажмите **«Подключиться»**.
3. Выберите способ входа:
   - **«Гость»:** только имя, работает только на этом ПК.
   - **«Вход» / «Регистрация»:** логин и пароль, вход с любого компьютера. Гостя позже можно превратить в аккаунт
     без потери статистики.
4. На странице **«Пати»** создайте пати и отправьте друзьям код `XXXX-XXXX` или войдите по чужому коду.
5. Общий матч начинается и заканчивается клавишами F9/F10 владельца.

**Страница «Сервер»:**
- Статус и пинг.
- Подключение и отключение.
- Автоподключение при запуске и автоматическое переподключение.
- Список сохранённых серверов.
- Понятные сообщения об ошибках: сервер недоступен, проблема с сертификатом, неверный пароль, устаревшая версия.

**Без связи.** Если соединение пропало, сундуки сохраняются на диске и отправляются автоматически после
восстановления.

**Приватность.**
- На сервер передаются только события сундуков (время, +1/−1, источник).
- Пароль не сохраняется, ключи устройства зашифрованы Windows DPAPI.
- Данные Fortnite и Epic не используются.
- Программа не читает и не изменяет игру.
