using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Disconnect To Home", "Ryz0r, Plumbus", "2.0.1")]
    [Description("Moves a sleeping player to their home point after they disconnect. Plumbus-style GUI, EN/UK.")]
    public class DisconnectToHome : RustPlugin
    {
        #region Константи

        private const string PermUse = "disconnecttohome.use";
        private const string PermAdmin = "disconnecttohome.admin";
        private const string UiRoot = "DisconnectToHome.Root";
        private const string UiWin = "DisconnectToHome.Win";
        private const string Cmd = "dth";

        private const string FontBold = "robotocondensed-bold.ttf";
        private const string FontReg = "robotocondensed-regular.ttf";

        private const string cOverlay = "0 0 0 0.55";
        private const string cBg = "0.12 0.12 0.12 0.98";
        private const string cHeader = "0.15 0.15 0.15 1";
        private const string cCard = "0.17 0.17 0.17 1";
        private const string cSlot = "0.23 0.23 0.23 1";
        private const string cLine = "0.26 0.26 0.26 1";
        private const string cFooter = "0.10 0.10 0.10 1";
        private const string cPink = "0.93 0.42 0.78 1";
        private const string cPinkBg = "0.30 0.17 0.28 1";
        private const string cPinkBtn = "0.56 0.24 0.52 1";
        private const string cText = "0.93 0.93 0.93 1";
        private const string cMuted = "0.60 0.60 0.60 1";
        private const string cGreen = "0.55 0.85 0.35 1";
        private const string cGreenBg = "0.20 0.32 0.16 1";
        private const string cRed = "0.92 0.36 0.36 1";
        private const string cRedBg = "0.38 0.15 0.15 1";
        private const string cBtn = "0.22 0.22 0.22 1";
        private const string cDisabled = "0.20 0.20 0.20 0.9";
        private const string cClear = "0 0 0 0";

        private const int AdminRowsPerPage = 7;

        #endregion

        #region Конфіг

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("Chat commands", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Commands = new List<string> { "disconnecthere", "dhome" };

            [JsonProperty("Require tool cupboard (TC) authorization to set and teleport")]
            public bool RequireTcAuth = true;

            [JsonProperty("Clear all points on wipe")]
            public bool WipeOnNewSave = true;

            [JsonProperty("Header icon (item shortname)")]
            public string HeaderIcon = "sleepingbag";

            [JsonProperty("Subtitle (pink)")]
            public string Subtitle = "PLUMBUS RUST";

        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
                // прибираємо дублікати команд (старі версії дописували "menu" у список при кожному завантаженні)
                config.Commands = (config.Commands ?? new List<string>())
                    .Select(x => (x ?? "").Trim().TrimStart('/'))
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                SaveConfig();
            }
            catch
            {
                PrintError("Config is invalid, using defaults (file was not overwritten).");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Дані

        private PluginData data;

        private class PluginData
        {
            [JsonProperty("Wipe Id")]
            public string WipeId = "";

            [JsonProperty("Homes")]
            public Dictionary<string, HomePoint> Homes = new Dictionary<string, HomePoint>();
        }

        // Vector3 напряму через Newtonsoft серіалізується погано — зберігаємо x/y/z окремо
        private class HomePoint
        {
            public float X, Y, Z;
            public string Name = "";
            public long SetAt;

            [JsonIgnore]
            public Vector3 Position
            {
                get { return new Vector3(X, Y, Z); }
                set { X = value.x; Y = value.y; Z = value.z; }
            }
        }

        private void LoadData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name); }
            catch (Exception e) { PrintError("Failed to read data: " + e.Message); }
            if (data == null) data = new PluginData();
            if (data.Homes == null) data.Homes = new Dictionary<string, HomePoint>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, data);

        private static string CurrentWipeId()
        {
            try { return SaveRestore.SaveCreatedTime.ToUniversalTime().Ticks.ToString(); }
            catch { return ""; }
        }

        private void ClearForWipe(string reason)
        {
            int count = data.Homes.Count;
            data.Homes.Clear();
            data.WipeId = CurrentWipeId();
            SaveData();
            Puts($"Wipe ({reason}): removed {count} home points");
        }

        #endregion

        #region Мова

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#ee6bc7>[Home]</color> ",
                ["NoPerm"] = "You do not have access to this feature.",
                ["FoundationOrFloor"] = "You must stand on a foundation or a floor.",
                ["NotAuthed"] = "You must be authorized on the tool cupboard (TC) of this base.",
                ["Set"] = "Logout point set. When you leave the game you will end up here.",
                ["Moved"] = "Logout point moved to your current location.",
                ["Removed"] = "Logout point removed.",
                ["NotSet"] = "You have not set a logout point yet.",
                ["Teleported"] = "While you were away, you rested in your base.",
                ["Cancelled"] = "Teleport did not happen: {0}",
                ["ReasonDestroyed"] = "the foundation or floor was destroyed",
                ["ReasonNoAuth"] = "you are not authorized on the TC of this base",
                ["Help"] = "/{0} - open the menu\n/{0} set - set the point here\n/{0} remove - remove the point",

                ["UI.Section.Home"] = "HOME ON LOGOUT",
                ["UI.Title.Home"] = "Logout point",
                ["UI.Section.Admin"] = "HOME ON LOGOUT · ADMIN",
                ["UI.Title.Admin"] = "Player points",
                ["UI.Tab.Home"] = "⌂  HOME ON LOGOUT",
                ["UI.Tab.Admin"] = "⚙  ADMIN",
                ["UI.Page"] = "PAGE {0} / {1}",
                ["UI.Card.Point"] = "YOUR LOGOUT POINT",
                ["UI.Status.NotSet"] = "NOT SET",
                ["UI.Status.Active"] = "✔ ACTIVE",
                ["UI.Status.Invalid"] = "⚠ INVALID",
                ["UI.Hint.NotSet"] = "Stand on a foundation or floor in your base and press \"Set here\".",
                ["UI.Grid"] = "Grid: {0}",
                ["UI.Row.SetAt"] = "Set at",
                ["UI.Row.Distance"] = "Distance from you",
                ["UI.Meters"] = "{0} m",
                ["UI.Invalid"] = "Teleport will not work now: {0}.",
                ["UI.Btn.Set"] = "📍  SET HERE",
                ["UI.Btn.Move"] = "📍  MOVE HERE",
                ["UI.Btn.Remove"] = "✕  REMOVE",
                ["UI.Btn.RemoveConfirm"] = "REALLY REMOVE?",
                ["UI.Card.Check"] = "CHECK THIS SPOT",
                ["UI.Check.Floor"] = "You are standing on a foundation or floor",
                ["UI.Check.Auth"] = "You are authorized on the TC of this base",
                ["UI.Card.Rules"] = "HOW IT WORKS",
                ["UI.Rule.Teleport"] = "• When you leave the game, your sleeping character is moved to this point.",
                ["UI.Rule.PlaceAuth"] = "• The point can only be placed on a foundation or floor in a base where you are authorized on the TC.",
                ["UI.Rule.Place"] = "• The point can only be placed on a foundation or floor.",
                ["UI.Rule.BrokenAuth"] = "• If the base is destroyed or you are removed from the TC, the teleport will not happen.",
                ["UI.Rule.Broken"] = "• If the base is destroyed, the teleport will not happen.",
                ["UI.Rule.Wipe"] = "• All points are removed automatically after a wipe.",
                ["UI.Footer.Home"] = "Walk to the spot you want and open the menu again to refresh the check.",
                ["UI.Admin.Total"] = "Total points: {0}",
                ["UI.Admin.ClearAll"] = "✕  CLEAR ALL",
                ["UI.Admin.ClearAllConfirm"] = "REALLY CLEAR ALL?",
                ["UI.Admin.Empty"] = "No player has set a point yet",
                ["UI.Admin.Intact"] = "base intact",
                ["UI.Admin.Destroyed"] = "destroyed",
                ["UI.Admin.Tp"] = "TP",
                ["UI.Admin.Confirm"] = "SURE?",
                ["UI.Admin.Deleted"] = "Player point removed.",
                ["UI.Admin.Cleared"] = "All points removed: {0}.",
                ["UI.Footer.Admin"] = "Green bar - foundation intact, red - point destroyed. Press ✕ again to confirm."
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#ee6bc7>[Дім]</color> ",
                ["NoPerm"] = "У вас немає доступу до цієї функції.",
                ["FoundationOrFloor"] = "Потрібно стояти на фундаменті або підлозі.",
                ["NotAuthed"] = "Потрібна авторизація в шафі (TC) цього будинку.",
                ["Set"] = "Точку виходу встановлено. Після виходу з гри ви опинитесь тут.",
                ["Moved"] = "Точку виходу перенесено на поточне місце.",
                ["Removed"] = "Точку виходу видалено.",
                ["NotSet"] = "Точку виходу ще не встановлено.",
                ["Teleported"] = "Поки вас не було, ви відпочивали у своєму будинку.",
                ["Cancelled"] = "Перенесення не спрацювало: {0}",
                ["ReasonDestroyed"] = "фундамент або підлогу зруйновано",
                ["ReasonNoAuth"] = "вас немає в шафі (TC) цього будинку",
                ["Help"] = "/{0} — відкрити меню\n/{0} set — встановити точку тут\n/{0} remove — видалити точку",

                ["UI.Section.Home"] = "ДІМ ПРИ ВИХОДІ",
                ["UI.Title.Home"] = "Точка виходу з гри",
                ["UI.Section.Admin"] = "ДІМ ПРИ ВИХОДІ · АДМІНКА",
                ["UI.Title.Admin"] = "Точки гравців",
                ["UI.Tab.Home"] = "⌂  ДІМ ПРИ ВИХОДІ",
                ["UI.Tab.Admin"] = "⚙  АДМІНКА",
                ["UI.Page"] = "СТОРІНКА {0} / {1}",
                ["UI.Card.Point"] = "ВАША ТОЧКА ВИХОДУ",
                ["UI.Status.NotSet"] = "НЕ ВСТАНОВЛЕНА",
                ["UI.Status.Active"] = "✔ АКТИВНА",
                ["UI.Status.Invalid"] = "⚠ НЕДІЙСНА",
                ["UI.Hint.NotSet"] = "Станьте на фундамент або підлогу у своєму будинку й натисніть «Встановити тут».",
                ["UI.Grid"] = "Квадрат: {0}",
                ["UI.Row.SetAt"] = "Встановлено",
                ["UI.Row.Distance"] = "Відстань від вас",
                ["UI.Meters"] = "{0} м",
                ["UI.Invalid"] = "Перенесення зараз не спрацює: {0}.",
                ["UI.Btn.Set"] = "📍  ВСТАНОВИТИ ТУТ",
                ["UI.Btn.Move"] = "📍  ПЕРЕНЕСТИ СЮДИ",
                ["UI.Btn.Remove"] = "✕  ВИДАЛИТИ",
                ["UI.Btn.RemoveConfirm"] = "ТОЧНО ВИДАЛИТИ?",
                ["UI.Card.Check"] = "ПЕРЕВІРКА ЦЬОГО МІСЦЯ",
                ["UI.Check.Floor"] = "Ви стоїте на фундаменті або підлозі",
                ["UI.Check.Auth"] = "Ви авторизовані в шафі (TC) цього будинку",
                ["UI.Card.Rules"] = "ЯК ЦЕ ПРАЦЮЄ",
                ["UI.Rule.Teleport"] = "• Коли ви виходите з гри, ваш сплячий персонаж переноситься на цю точку.",
                ["UI.Rule.PlaceAuth"] = "• Точку можна поставити лише на фундаменті чи підлозі у будинку, де ви авторизовані в шафі.",
                ["UI.Rule.Place"] = "• Точку можна поставити лише на фундаменті чи підлозі.",
                ["UI.Rule.BrokenAuth"] = "• Якщо будинок зруйновано або вас виписали з шафи, перенесення не спрацює.",
                ["UI.Rule.Broken"] = "• Якщо будинок зруйновано, перенесення не спрацює.",
                ["UI.Rule.Wipe"] = "• Після вайпу всі точки автоматично видаляються.",
                ["UI.Footer.Home"] = "Підійдіть до потрібного місця та відкрийте меню знову, щоб оновити перевірку.",
                ["UI.Admin.Total"] = "Усього точок: {0}",
                ["UI.Admin.ClearAll"] = "✕  ОЧИСТИТИ ВСІ",
                ["UI.Admin.ClearAllConfirm"] = "ТОЧНО ВИДАЛИТИ ВСІ?",
                ["UI.Admin.Empty"] = "Жоден гравець ще не встановив точку",
                ["UI.Admin.Intact"] = "будинок на місці",
                ["UI.Admin.Destroyed"] = "зруйновано",
                ["UI.Admin.Tp"] = "ТП",
                ["UI.Admin.Confirm"] = "ТОЧНО?",
                ["UI.Admin.Deleted"] = "Точку гравця видалено.",
                ["UI.Admin.Cleared"] = "Видалено всі точки: {0}.",
                ["UI.Footer.Admin"] = "Зелена смуга — фундамент на місці, червона — точку зруйновано. Повторне ✕ підтверджує видалення."
            }, this, "uk");
        }

        private string Msg(string key, BasePlayer player, params object[] args)
        {
            var text = lang.GetMessage(key, this, player?.UserIDString);
            return args != null && args.Length > 0 ? string.Format(text, args) : text;
        }

        private void Reply(BasePlayer player, string key, params object[] args) =>
            player.ChatMessage(Msg("Prefix", player) + Msg(key, player, args));

        #endregion

        #region Стан UI

        private class UiState
        {
            public string Page = "home";
            public int AdminPage;
            public string PendingDelete;
            public string Notice;
            public bool NoticeError;
        }

        private readonly Dictionary<ulong, UiState> states = new Dictionary<ulong, UiState>();
        private readonly Dictionary<string, string> lastResult = new Dictionary<string, string>();

        private UiState GetState(BasePlayer player)
        {
            UiState st;
            if (!states.TryGetValue(player.userID, out st)) states[player.userID] = st = new UiState();
            return st;
        }

        #endregion

        #region Хуки

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            foreach (var c in config.Commands)
                cmd.AddChatCommand(c, this, nameof(CmdChat));
        }

        private void OnServerInitialized()
        {
            if (!config.WipeOnNewSave) return;
            var wipeId = CurrentWipeId();
            if (string.IsNullOrEmpty(wipeId)) return;

            if (string.IsNullOrEmpty(data.WipeId))
            {
                data.WipeId = wipeId;
                SaveData();
            }
            else if (data.WipeId != wipeId)
            {
                ClearForWipe("new map");
            }
        }

        private void OnNewSave(string filename)
        {
            if (config.WipeOnNewSave) ClearForWipe("OnNewSave");
        }

        private void OnServerSave() => SaveData();

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) CuiHelper.DestroyUi(p, UiRoot);
            SaveData();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            states.Remove(player.userID);

            HomePoint hp;
            if (!HasUse(player) || !data.Homes.TryGetValue(player.UserIDString, out hp)) return;

            string fail = ValidateTeleport(player, hp);
            lastResult[player.UserIDString] = fail ?? "ok";
            if (fail != null) return;

            var pos = hp.Position + new Vector3(0f, 0.1f, 0f);
            NextTick(() =>
            {
                if (player == null || player.IsDestroyed) return;
                player.Teleport(pos);
                player.SendNetworkUpdateImmediate();
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            string result;
            if (player == null || !lastResult.TryGetValue(player.UserIDString, out result)) return;
            lastResult.Remove(player.UserIDString);

            timer.Once(3f, () =>
            {
                if (player == null || !player.IsConnected) return;
                if (result == "ok") Reply(player, "Teleported");
                else Reply(player, "Cancelled", Msg(result, player));
            });
        }

        #endregion

        #region Логіка

        private bool HasUse(BasePlayer p) => permission.UserHasPermission(p.UserIDString, PermUse);
        private bool IsAdmin(BasePlayer p) => permission.UserHasPermission(p.UserIDString, PermAdmin);

        // Будівельний блок (фундамент/підлога) під точкою
        private static BaseEntity FloorAt(Vector3 position)
        {
            RaycastHit hit;
            if (!Physics.Raycast(position + new Vector3(0f, 0.2f, 0f), Vector3.down, out hit, 3f, Layers.Mask.Construction))
                return null;
            var entity = hit.GetEntity();
            if (entity == null || entity.IsDestroyed) return null;
            var prefab = entity.ShortPrefabName ?? "";
            return prefab.Contains("floor") || prefab.Contains("foundation") ? entity : null;
        }

        private static bool IsAuthedAt(BasePlayer player, BaseEntity floor)
        {
            if (floor == null) return false;
            var priv = floor.GetBuildingPrivilege();
            return priv != null && priv.IsAuthed(player);
        }

        // null = можна переносити, інакше ключ причини
        private string ValidateTeleport(BasePlayer player, HomePoint hp)
        {
            var floor = FloorAt(hp.Position);
            if (floor == null) return "ReasonDestroyed";
            if (config.RequireTcAuth && !IsAuthedAt(player, floor)) return "ReasonNoAuth";
            return null;
        }

        // null = успіх, інакше ключ помилки
        private string TrySet(BasePlayer player)
        {
            if (!HasUse(player)) return "NoPerm";
            var floor = FloorAt(player.transform.position);
            if (floor == null) return "FoundationOrFloor";
            if (config.RequireTcAuth && !IsAuthedAt(player, floor)) return "NotAuthed";

            bool existed = data.Homes.ContainsKey(player.UserIDString);
            data.Homes[player.UserIDString] = new HomePoint
            {
                Position = player.transform.position,
                Name = player.displayName,
                SetAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            SaveData();
            return existed ? "+Moved" : "+Set";
        }

        private string TryRemove(BasePlayer player)
        {
            if (!data.Homes.Remove(player.UserIDString)) return "NotSet";
            SaveData();
            return "+Removed";
        }

        #endregion

        #region Команди

        private void CmdChat(BasePlayer player, string command, string[] args)
        {
            if (!HasUse(player) && !IsAdmin(player)) { Reply(player, "NoPerm"); return; }

            if (args == null || args.Length == 0)
            {
                GetState(player).Page = "home";
                Draw(player);
                return;
            }

            string sub = args[0].ToLowerInvariant();
            string result;
            if (sub == "set") result = TrySet(player);
            else if (sub == "remove") result = TryRemove(player);
            else { player.ChatMessage(Msg("Prefix", player) + Msg("Help", player, command)); return; }

            Reply(player, result.TrimStart('+'));
        }

        [ConsoleCommand(Cmd)]
        private void CmdConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs()) return;

            // arg.Args у нових версіях Rust — StringView[], тому беремо через GetString
            int n = arg.Args.Length;
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = arg.GetString(i, "") ?? "";

            var st = GetState(player);
            string action = a[0];

            if (action == "close")
            {
                CuiHelper.DestroyUi(player, UiRoot);
                st.Notice = null;
                return;
            }

            st.Notice = null;
            st.NoticeError = false;

            if (action == "home")
            {
                st.Page = "home";
                st.PendingDelete = null;
            }
            else if (action == "set")
            {
                ApplyResult(player, st, TrySet(player));
                st.PendingDelete = null;
            }
            else if (action == "remove")
            {
                if (st.PendingDelete == "self")
                {
                    ApplyResult(player, st, TryRemove(player));
                    st.PendingDelete = null;
                }
                else st.PendingDelete = "self";
            }
            else if (action == "admin")
            {
                if (!IsAdmin(player)) return;
                HandleAdmin(player, st, a);
            }
            else return;

            Draw(player);
        }

        private void HandleAdmin(BasePlayer player, UiState st, string[] a)
        {
            string sub = a.Length > 1 ? a[1] : "";
            string id = a.Length > 2 ? a[2] : "";

            if (sub == "page")
            {
                st.Page = "admin";
                int p;
                st.AdminPage = int.TryParse(id, out p) ? p : 0;
                st.PendingDelete = null;
            }
            else if (sub == "tp")
            {
                HomePoint hp;
                if (data.Homes.TryGetValue(id, out hp))
                {
                    player.Teleport(hp.Position + new Vector3(0f, 0.1f, 0f));
                }
            }
            else if (sub == "del")
            {
                if (st.PendingDelete == id)
                {
                    data.Homes.Remove(id);
                    SaveData();
                    st.PendingDelete = null;
                    st.Notice = Msg("UI.Admin.Deleted", player);
                }
                else st.PendingDelete = id;
            }
            else if (sub == "clear")
            {
                if (st.PendingDelete == "__all")
                {
                    int count = data.Homes.Count;
                    data.Homes.Clear();
                    SaveData();
                    st.PendingDelete = null;
                    st.Notice = Msg("UI.Admin.Cleared", player, count);
                }
                else st.PendingDelete = "__all";
            }
        }

        private void ApplyResult(BasePlayer player, UiState st, string result)
        {
            bool ok = result.StartsWith("+");
            st.Notice = Msg(result.TrimStart('+'), player);
            st.NoticeError = !ok;
        }

        #endregion

        #region UI — каркас

        private void Draw(BasePlayer player)
        {
            var st = GetState(player);
            if (st.Page == "admin" && !IsAdmin(player)) st.Page = "home";

            CuiHelper.DestroyUi(player, UiRoot);
            var c = new CuiElementContainer();

            c.Add(new CuiElement
            {
                Name = UiRoot,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent { Color = cOverlay, Material = "assets/content/ui/uibackgroundblur.mat" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                    new CuiNeedsCursorComponent()
                }
            });
            c.Add(new CuiButton
            {
                Button = { Color = cClear, Command = $"{Cmd} close" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = "" }
            }, UiRoot, CuiHelper.GetGuid());

            c.Add(new CuiElement
            {
                Name = UiWin,
                Parent = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = cBg },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-480 -300", OffsetMax = "480 300" },
                    new CuiOutlineComponent { Color = cLine, Distance = "1 -1" }
                }
            });
            Btn(c, UiWin, 0, 0, 960, 600, cClear, "", "", 1, cClear);

            if (st.Page == "admin") DrawAdmin(c, player, st);
            else DrawHome(c, player, st);

            CuiHelper.AddUi(player, c);
        }

        private void DrawHeader(CuiElementContainer c, string section, string title)
        {
            Panel(c, UiWin, 0, 0, 960, 70, cHeader);
            Panel(c, UiWin, 18, 10, 50, 50, cPinkBg);
            var icon = ItemManager.FindItemDefinition(config.HeaderIcon);
            if (icon != null) Icon(c, UiWin, 22, 14, 42, 42, icon.itemid);
            Txt(c, UiWin, 82, 12, 600, 18, $"{config.Subtitle} · {section}", 11, cPink);
            Txt(c, UiWin, 82, 28, 700, 32, title, 22, cText);
            Btn(c, UiWin, 905, 15, 40, 40, cBtn, $"{Cmd} close", "✕", 18, cText);
        }

        private void DrawTabs(CuiElementContainer c, BasePlayer player, string active)
        {
            float x = 20f;
            Tab(c, ref x, Msg("UI.Tab.Home", player), $"{Cmd} home", active == "home");
            if (IsAdmin(player)) Tab(c, ref x, Msg("UI.Tab.Admin", player), $"{Cmd} admin page 0", active == "admin");
            Panel(c, UiWin, 0, 116, 960, 1, cLine);
        }

        private void Tab(CuiElementContainer c, ref float x, string text, string cmd, bool active)
        {
            const float w = 180f;
            if (active)
            {
                Panel(c, UiWin, x, 78, w, 36, "0.18 0.18 0.18 1");
                Panel(c, UiWin, x + 20, 112, w - 40, 3, cPink);
            }
            Btn(c, UiWin, x, 78, w, 36, cClear, cmd, text, 14, active ? cText : cMuted);
            x += w + 10;
        }

        private void DrawFooter(CuiElementContainer c, BasePlayer player, UiState st, string info, int page = 0, int pages = 1, string pageCmd = "")
        {
            Panel(c, UiWin, 0, 550, 960, 50, cFooter);
            if (!string.IsNullOrEmpty(st.Notice))
                Txt(c, UiWin, 24, 550, 640, 50, (st.NoticeError ? "✖  " : "✔  ") + st.Notice, 13, st.NoticeError ? cRed : cGreen);
            else
                Txt(c, UiWin, 24, 550, 640, 50, "ⓘ  " + info, 12, cMuted, TextAnchor.MiddleLeft, false);

            if (pages <= 1) return;
            Txt(c, UiWin, 640, 550, 170, 50, Msg("UI.Page", player, page + 1, pages), 12, cMuted, TextAnchor.MiddleRight);
            bool hasPrev = page > 0, hasNext = page < pages - 1;
            Btn(c, UiWin, 822, 558, 56, 34, hasPrev ? cBtn : cDisabled, hasPrev ? $"{pageCmd} {page - 1}" : "", "‹", 20, hasPrev ? cText : cMuted);
            Btn(c, UiWin, 884, 558, 56, 34, hasNext ? cBtn : cDisabled, hasNext ? $"{pageCmd} {page + 1}" : "", "›", 20, hasNext ? cText : cMuted);
        }

        #endregion

        #region UI — гравець

        private void DrawHome(CuiElementContainer c, BasePlayer player, UiState st)
        {
            DrawHeader(c, Msg("UI.Section.Home", player), Msg("UI.Title.Home", player));
            DrawTabs(c, player, "home");

            HomePoint hp;
            bool has = data.Homes.TryGetValue(player.UserIDString, out hp);
            string invalid = null;
            if (has)
            {
                var floor = FloorAt(hp.Position);
                if (floor == null) invalid = Msg("ReasonDestroyed", player);
                else if (config.RequireTcAuth && !IsAuthedAt(player, floor)) invalid = Msg("ReasonNoAuth", player);
            }

            // ліва картка — поточна точка
            var left = Panel(c, UiWin, 20, 128, 450, 410, cCard, null, true);
            Txt(c, left, 16, 10, 400, 22, Msg("UI.Card.Point", player), 12, cPink);

            Panel(c, left, 16, 40, 90, 90, cSlot);
            var bag = ItemManager.FindItemDefinition("sleepingbag");
            if (bag != null) Icon(c, left, 24, 48, 74, 74, bag.itemid);

            if (!has)
            {
                Badge(c, left, 120, 44, 150, Msg("UI.Status.NotSet", player), cBtn, cMuted);
                Txt(c, left, 120, 74, 310, 56, Msg("UI.Hint.NotSet", player), 12, cMuted, TextAnchor.UpperLeft, false);
            }
            else
            {
                if (invalid == null) Badge(c, left, 120, 44, 130, Msg("UI.Status.Active", player), cGreenBg, cGreen);
                else Badge(c, left, 120, 44, 130, Msg("UI.Status.Invalid", player), cRedBg, cRed);

                Txt(c, left, 120, 74, 310, 26, Msg("UI.Grid", player, Grid(hp.Position)), 17, cText);
                Txt(c, left, 120, 100, 310, 20, $"{hp.X:0} / {hp.Y:0} / {hp.Z:0}", 11, cMuted, TextAnchor.MiddleLeft, false);

                InfoRow(c, left, 150, Msg("UI.Row.SetAt", player), DateTimeOffset.FromUnixTimeSeconds(hp.SetAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
                InfoRow(c, left, 186, Msg("UI.Row.Distance", player), Msg("UI.Meters", player, Vector3.Distance(player.transform.position, hp.Position).ToString("0")));
                if (invalid != null)
                    Txt(c, left, 16, 230, 418, 40, Msg("UI.Invalid", player, invalid), 12, cRed, TextAnchor.UpperLeft, false);
            }

            Btn(c, left, 16, 352, 205, 42, cPinkBtn, $"{Cmd} set", Msg(has ? "UI.Btn.Move" : "UI.Btn.Set", player), 13, cText);
            bool pending = st.PendingDelete == "self";
            if (has)
                Btn(c, left, 229, 352, 205, 42, pending ? cRed : cRedBg, $"{Cmd} remove", Msg(pending ? "UI.Btn.RemoveConfirm" : "UI.Btn.Remove", player), 13, cText);
            else
                Btn(c, left, 229, 352, 205, 42, cDisabled, "", Msg("UI.Btn.Remove", player), 13, cMuted);

            // права картка — перевірка місця та правила
            var right = Panel(c, UiWin, 490, 128, 450, 410, cCard, null, true);
            Txt(c, right, 16, 10, 400, 22, Msg("UI.Card.Check", player), 12, cPink);

            var here = FloorAt(player.transform.position);
            Check(c, right, 40, here != null, Msg("UI.Check.Floor", player));
            if (config.RequireTcAuth) Check(c, right, 80, IsAuthedAt(player, here), Msg("UI.Check.Auth", player));

            float rulesY = config.RequireTcAuth ? 132 : 92;
            Txt(c, right, 16, rulesY, 400, 22, Msg("UI.Card.Rules", player), 12, cPink);
            var rules = Msg("UI.Rule.Teleport", player) + "\n" +
                        Msg(config.RequireTcAuth ? "UI.Rule.PlaceAuth" : "UI.Rule.Place", player) + "\n" +
                        Msg(config.RequireTcAuth ? "UI.Rule.BrokenAuth" : "UI.Rule.Broken", player) +
                        (config.WipeOnNewSave ? "\n" + Msg("UI.Rule.Wipe", player) : "");
            Txt(c, right, 16, rulesY + 26, 418, 240, rules, 12, cText, TextAnchor.UpperLeft, false);

            DrawFooter(c, player, st, Msg("UI.Footer.Home", player));
        }

        private void Badge(CuiElementContainer c, string parent, float x, float y, float w, string text, string bg, string fg)
        {
            Panel(c, parent, x, y, w, 24, bg);
            Txt(c, parent, x, y, w, 24, text, 11, fg, TextAnchor.MiddleCenter);
        }

        private void InfoRow(CuiElementContainer c, string parent, float y, string label, string value)
        {
            Panel(c, parent, 16, y, 418, 30, "0.14 0.14 0.14 1");
            Txt(c, parent, 28, y, 200, 30, label, 12, cMuted, TextAnchor.MiddleLeft, false);
            Txt(c, parent, 220, y, 202, 30, value, 13, cText, TextAnchor.MiddleRight);
        }

        private void Check(CuiElementContainer c, string parent, float y, bool ok, string text)
        {
            Panel(c, parent, 16, y, 418, 34, "0.14 0.14 0.14 1");
            Panel(c, parent, 16, y, 4, 34, ok ? cGreen : cRed);
            Txt(c, parent, 30, y, 30, 34, ok ? "✔" : "✖", 15, ok ? cGreen : cRed, TextAnchor.MiddleCenter);
            Txt(c, parent, 64, y, 360, 34, text, 12, ok ? cText : cMuted, TextAnchor.MiddleLeft, false);
        }

        #endregion

        #region UI — адмінка

        private void DrawAdmin(CuiElementContainer c, BasePlayer player, UiState st)
        {
            DrawHeader(c, Msg("UI.Section.Admin", player), Msg("UI.Title.Admin", player));
            DrawTabs(c, player, "admin");

            var list = data.Homes.OrderBy(kv => kv.Value.Name).ToList();
            Txt(c, UiWin, 20, 124, 500, 32, Msg("UI.Admin.Total", player, list.Count), 13, cMuted, TextAnchor.MiddleLeft, false);
            bool clearPending = st.PendingDelete == "__all";
            if (list.Count > 0)
                Btn(c, UiWin, 740, 124, 200, 32, clearPending ? cRed : cRedBg, $"{Cmd} admin clear",
                    Msg(clearPending ? "UI.Admin.ClearAllConfirm" : "UI.Admin.ClearAll", player), 12, cText);

            int pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)AdminRowsPerPage));
            st.AdminPage = Mathf.Clamp(st.AdminPage, 0, pages - 1);

            if (list.Count == 0)
                Txt(c, UiWin, 0, 170, 960, 360, Msg("UI.Admin.Empty", player), 16, cMuted, TextAnchor.MiddleCenter);

            var page = list.Skip(st.AdminPage * AdminRowsPerPage).Take(AdminRowsPerPage).ToList();
            for (int i = 0; i < page.Count; i++)
            {
                var id = page[i].Key;
                var hp = page[i].Value;
                var row = Panel(c, UiWin, 20, 166 + i * 54, 920, 48, cCard, null, true);

                bool valid = FloorAt(hp.Position) != null;
                Panel(c, row, 0, 0, 4, 48, valid ? cGreen : cRed);
                Txt(c, row, 16, 4, 300, 22, string.IsNullOrEmpty(hp.Name) ? id : hp.Name, 15, cText);
                Txt(c, row, 16, 25, 300, 18, id, 10, cMuted, TextAnchor.MiddleLeft, false);
                Txt(c, row, 330, 0, 90, 48, Grid(hp.Position), 15, cText, TextAnchor.MiddleCenter);
                Txt(c, row, 430, 0, 200, 48, $"{hp.X:0} / {hp.Y:0} / {hp.Z:0}", 11, cMuted, TextAnchor.MiddleLeft, false);
                Txt(c, row, 620, 0, 120, 48, Msg(valid ? "UI.Admin.Intact" : "UI.Admin.Destroyed", player), 11, valid ? cGreen : cRed, TextAnchor.MiddleLeft, false);

                Btn(c, row, 760, 9, 70, 30, cPinkBg, $"{Cmd} admin tp {id}", Msg("UI.Admin.Tp", player), 12, cPink);
                bool pending = st.PendingDelete == id;
                Btn(c, row, 836, 9, 74, 30, pending ? cRed : cRedBg, $"{Cmd} admin del {id}", pending ? Msg("UI.Admin.Confirm", player) : "✕", pending ? 11 : 14, cText);
            }

            DrawFooter(c, player, st, Msg("UI.Footer.Admin", player),
                st.AdminPage, pages, $"{Cmd} admin page");
        }

        #endregion

        #region UI — хелпери

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Grid(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return "?"; }
        }

        private static CuiRectTransformComponent R(float x, float y, float w, float h) => new CuiRectTransformComponent
        {
            AnchorMin = "0 1",
            AnchorMax = "0 1",
            OffsetMin = $"{F(x)} {F(-(y + h))}",
            OffsetMax = $"{F(x + w)} {F(-y)}"
        };

        private static string Panel(CuiElementContainer c, string parent, float x, float y, float w, float h, string color, string name = null, bool outline = false)
        {
            name = name ?? CuiHelper.GetGuid();
            var el = new CuiElement { Name = name, Parent = parent };
            el.Components.Add(new CuiImageComponent { Color = color });
            el.Components.Add(R(x, y, w, h));
            if (outline) el.Components.Add(new CuiOutlineComponent { Color = "0.25 0.25 0.25 1", Distance = "1 -1" });
            c.Add(el);
            return name;
        }

        private static void Txt(CuiElementContainer c, string parent, float x, float y, float w, float h, string text, int size, string color,
            TextAnchor align = TextAnchor.MiddleLeft, bool bold = true)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text ?? "", FontSize = size, Color = color, Align = align, Font = bold ? FontBold : FontReg },
                    R(x, y, w, h)
                }
            });
        }

        private static void Btn(CuiElementContainer c, string parent, float x, float y, float w, float h, string color, string cmd,
            string text, int size, string textColor, TextAnchor align = TextAnchor.MiddleCenter)
        {
            c.Add(new CuiButton
            {
                Button = { Color = color, Command = cmd },
                RectTransform =
                {
                    AnchorMin = "0 1",
                    AnchorMax = "0 1",
                    OffsetMin = $"{F(x)} {F(-(y + h))}",
                    OffsetMax = $"{F(x + w)} {F(-y)}"
                },
                Text = { Text = text ?? "", FontSize = size, Color = textColor, Align = align, Font = FontBold }
            }, parent, CuiHelper.GetGuid());
        }

        private static void Icon(CuiElementContainer c, string parent, float x, float y, float w, float h, int itemId, ulong skin = 0)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { ItemId = itemId, SkinId = skin },
                    R(x, y, w, h)
                }
            });
        }

        #endregion
    }
}
