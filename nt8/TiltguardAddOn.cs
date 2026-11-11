#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
#endregion

// Tilt Guard for NinjaTrader 8 — reports every connected account (Replikanto leader + followers)
// to the Tilt Guard server and enforces its locks: cancels new entry orders while blocked and
// flattens when a lock says so. Exits (orders that reduce a position) are always allowed.
// Config: Documents\NinjaTrader 8\tiltguard.txt with two lines:  url=http://127.0.0.1:8940  key=tg_...
namespace NinjaTrader.NinjaScript.AddOns
{
	public class TiltguardAddOn : AddOnBase
	{
		private NTMenuItem menuItem;
		private NTMenuItem newMenu;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "Tilt Guard";
				Description = "Tilt lockout: reports accounts to Tilt Guard and enforces its locks.";
			}
			else if (State == State.Active)
				TiltguardEngine.Start();
			else if (State == State.Terminated)
				TiltguardEngine.Stop();
		}

		protected override void OnWindowCreated(Window window)
		{
			ControlCenter cc = window as ControlCenter;
			if (cc == null)
				return;
			TiltguardEngine.Start();
			newMenu = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
			if (newMenu == null)
				return;
			menuItem = new NTMenuItem { Header = "Tilt Guard", Style = Application.Current.TryFindResource("MainMenuItem") as Style };
			newMenu.Items.Add(menuItem);
			menuItem.Click += OnMenuClick;
		}

		protected override void OnWindowDestroyed(Window window)
		{
			if (menuItem != null && window is ControlCenter)
			{
				if (newMenu != null && newMenu.Items.Contains(menuItem))
					newMenu.Items.Remove(menuItem);
				menuItem.Click -= OnMenuClick;
				menuItem = null;
			}
		}

		private void OnMenuClick(object sender, RoutedEventArgs e)
		{
			Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => new TiltguardWindow().Show()));
		}
	}

	public class GuardStatus
	{
		public string Name;
		public bool Locked, BlockEntries, Flatten;
		public string Reason = "", FlattenReason = "";
		public double? MaxDailyLoss, MaxTradeLoss, MaxContracts;
		public double DayPl, Realized, OpenPl;
		public int Trades;
		public double LockUntil;
	}

	public static class TiltguardEngine
	{
		public const string Version = "0.1.0";
		private static readonly object sync = new object();
		private static bool started;
		private static Timer timer;
		private static int busy;
		private static HttpClient http;
		private static string url = "", key = "";
		private static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
		private static readonly HashSet<Account> subscribed = new HashSet<Account>();
		private static readonly HashSet<string> sentExec = new HashSet<string>();
		private static readonly List<Dictionary<string, object>> pendingFills = new List<Dictionary<string, object>>();
		private static readonly List<Dictionary<string, object>> pendingEvents = new List<Dictionary<string, object>>();
		private static readonly Dictionary<string, GuardStatus> status = new Dictionary<string, GuardStatus>();
		private static readonly Dictionary<string, string> localLock = new Dictionary<string, string>();
		private static readonly Dictionary<string, DateTime> lastFlatten = new Dictionary<string, DateTime>();
		private static readonly HashSet<string> cancelled = new HashSet<string>();

		public static string LastError = "";
		public static DateTime LastOk = DateTime.MinValue;
		public static string ConfigPath { get { return Path.Combine(Core.Globals.UserDataDir, "tiltguard.txt"); } }

		public static void Start()
		{
			lock (sync)
			{
				if (started)
					return;
				started = true;
			}
			LoadConfig();
			http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
			Account.AccountStatusUpdate += OnAccountStatus;
			lock (Account.All)
				foreach (Account a in Account.All.ToList())
					Subscribe(a);
			timer = new Timer(Tick, null, 2000, 1500);
			Log("started v" + Version + (string.IsNullOrEmpty(key) ? " — no key yet, create " + ConfigPath : " -> " + url));
		}

		public static void Stop()
		{
			lock (sync)
			{
				if (!started)
					return;
				started = false;
			}
			if (timer != null) timer.Dispose();
			Account.AccountStatusUpdate -= OnAccountStatus;
			lock (subscribed)
			{
				foreach (Account a in subscribed)
				{
					a.ExecutionUpdate -= OnExecution;
					a.OrderUpdate -= OnOrder;
					a.AccountItemUpdate -= OnItem;
				}
				subscribed.Clear();
			}
			if (http != null) http.Dispose();
		}

		private static void LoadConfig()
		{
			try
			{
				if (!File.Exists(ConfigPath))
					return;
				foreach (string line in File.ReadAllLines(ConfigPath))
				{
					int i = line.IndexOf('=');
					if (i < 0) continue;
					string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
					if (k == "url") url = v.TrimEnd('/');
					if (k == "key") key = v;
				}
			}
			catch (Exception e) { LastError = "config: " + e.Message; }
		}

		public static void Log(string msg)
		{
			try { NinjaTrader.Code.Output.Process("[Tilt Guard] " + msg, PrintTo.OutputTab1); } catch { }
		}

		private static bool Watched(Account a)
		{
			return a != null && a.Name != "Backtest" && !a.Name.StartsWith("Playback");
		}

		private static bool Online(Account a)
		{
			return a.Connection != null && a.Connection.Status == ConnectionStatus.Connected;
		}

		private static void Subscribe(Account a)
		{
			if (!Watched(a))
				return;
			lock (subscribed)
			{
				if (!subscribed.Add(a))
					return;
			}
			a.ExecutionUpdate += OnExecution;
			a.OrderUpdate += OnOrder;
			a.AccountItemUpdate += OnItem;
			lock (a.Executions)
				foreach (Execution ex in a.Executions.ToList())
					QueueFill(ex);
		}

		private static void OnAccountStatus(object sender, AccountStatusEventArgs e)
		{
			if (e.Status == ConnectionStatus.Connected)
				Subscribe(e.Account);
		}

		// ------------------------------------------------ events
		private static void QueueFill(Execution ex)
		{
			if (ex == null || ex.Account == null || ex.Instrument == null)
				return;
			string id = ex.Account.Name + "|" + ex.ExecutionId;
			lock (pendingFills)
			{
				if (!sentExec.Add(id))
					return;
				DateTime utc = ex.Time.Kind == DateTimeKind.Utc ? ex.Time : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ex.Time, DateTimeKind.Unspecified), Core.Globals.GeneralOptions.TimeZoneInfo);
				pendingFills.Add(new Dictionary<string, object> {
					{ "account", ex.Account.Name }, { "exec_id", ex.ExecutionId }, { "order_id", ex.OrderId },
					{ "contract", ex.Instrument.FullName }, { "root", ex.Instrument.MasterInstrument.Name },
					{ "point_value", ex.Instrument.MasterInstrument.PointValue },
					{ "side", ex.MarketPosition == MarketPosition.Long ? 1 : -1 }, { "qty", ex.Quantity }, { "price", ex.Price },
					{ "ts", (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds }, { "commission", ex.Commission }
				});
			}
		}

		private static void QueueEvent(string account, string type, string detail)
		{
			lock (pendingEvents)
				pendingEvents.Add(new Dictionary<string, object> { { "account", account }, { "type", type }, { "detail", detail } });
			Log(account + " " + type + ": " + detail);
		}

		private static void OnExecution(object sender, ExecutionEventArgs e)
		{
			QueueFill(e.Execution);
		}

		private static void OnItem(object sender, AccountItemEventArgs e)
		{
			if (e.AccountItem != AccountItem.UnrealizedProfitLoss)
				return;
			Account a = sender as Account;
			GuardStatus g = Get(a);
			if (a == null || g == null)
				return;
			double open = e.Value;
			// instant check between two server round-trips: closed P&L (server, from fills) + live open P&L
			if (g.MaxDailyLoss.HasValue && !g.Locked && g.Realized + open <= -g.MaxDailyLoss.Value)
			{
				string why = string.Format(CultureInfo.InvariantCulture, "Daily loss limit hit: {0:0} (limit -{1:0})", g.Realized + open, g.MaxDailyLoss.Value);
				bool isNew;
				lock (localLock) { isNew = !localLock.ContainsKey(a.Name); localLock[a.Name] = why; }
				if (isNew) { QueueEvent(a.Name, "local_lock", why); Flatten(a, why); }
			}
			if (g.MaxTradeLoss.HasValue && open <= -g.MaxTradeLoss.Value)
				Flatten(a, string.Format(CultureInfo.InvariantCulture, "Max loss per trade ({0:0})", open));
		}

		private static void OnOrder(object sender, OrderEventArgs e)
		{
			Order o = e.Order;
			Account a = o != null ? o.Account : null;
			if (a == null || !(e.OrderState == OrderState.Submitted || e.OrderState == OrderState.Accepted || e.OrderState == OrderState.Working))
				return;
			string why = BlockReason(a, o);
			if (why == null)
				return;
			lock (cancelled)
			{
				if (!cancelled.Add(a.Name + "|" + o.OrderId + "|" + o.Id))
					return;
			}
			QueueEvent(a.Name, "blocked_order", o.Instrument.FullName + " " + o.OrderAction + " " + o.Quantity + " cancelled: " + why);
			ThreadPool.QueueUserWorkItem(_ => { try { a.Cancel(new[] { o }); } catch (Exception ex) { Log("cancel failed: " + ex.Message); } });
		}

		// ------------------------------------------------ rules
		private static GuardStatus Get(Account a)
		{
			if (a == null) return null;
			lock (status) { GuardStatus g; return status.TryGetValue(a.Name, out g) ? g : null; }
		}

		private static int PositionOf(Account a, Instrument inst)
		{
			lock (a.Positions)
			{
				Position p = a.Positions.FirstOrDefault(x => x.Instrument == inst);
				if (p == null || p.MarketPosition == MarketPosition.Flat) return 0;
				return p.MarketPosition == MarketPosition.Long ? p.Quantity : -p.Quantity;
			}
		}

		private static bool Reduces(Account a, Order o)
		{
			int pos = PositionOf(a, o.Instrument);
			bool sells = o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort;
			return pos != 0 && (pos > 0) == sells && o.Quantity <= Math.Abs(pos);
		}

		private static string BlockReason(Account a, Order o)
		{
			if (Reduces(a, o))
				return null;                                        // exits always allowed
			lock (localLock) { string l; if (localLock.TryGetValue(a.Name, out l)) return l; }
			GuardStatus g = Get(a);
			if (g == null)
				return null;
			if (g.BlockEntries)
				return string.IsNullOrEmpty(g.Reason) ? "Locked" : g.Reason;
			if (g.MaxContracts.HasValue)
			{
				int pos = PositionOf(a, o.Instrument);
				bool buys = o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover;
				int after = Math.Abs(pos + (buys ? o.Quantity : -o.Quantity));
				if (after > g.MaxContracts.Value)
					return "Max " + g.MaxContracts.Value + " contracts (would be " + after + ")";
			}
			return null;
		}

		public static void Flatten(Account a, string why, bool positionsToo = true)
		{
			lock (lastFlatten)
			{
				DateTime t;
				if (lastFlatten.TryGetValue(a.Name, out t) && (DateTime.UtcNow - t).TotalSeconds < 4)
					return;
				lastFlatten[a.Name] = DateTime.UtcNow;
			}
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					List<Order> orders;
					lock (a.Orders)
						orders = a.Orders.Where(o => o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
							|| o.OrderState == OrderState.Submitted || o.OrderState == OrderState.ChangePending).ToList();
					List<Instrument> open;
					lock (a.Positions)
						open = a.Positions.Where(p => p.MarketPosition != MarketPosition.Flat).Select(p => p.Instrument).ToList();
					int n = 0;
					if (positionsToo && open.Count > 0)
					{
						a.Flatten(open);                                    // closes positions + their orders
						n += open.Count;
					}
					List<Order> entries = orders.Where(o => positionsToo ? !open.Contains(o.Instrument) : !Reduces(a, o)).ToList();
					if (entries.Count > 0)
					{
						a.Cancel(entries);
						n += entries.Count;
					}
					if (n > 0)
						QueueEvent(a.Name, positionsToo ? "flatten" : "cancel_entries", n + " action(s): " + why);
				}
				catch (Exception ex) { Log("flatten failed on " + a.Name + ": " + ex.Message); }
			});
		}

		// ------------------------------------------------ server round-trip
		private static void Tick(object state)
		{
			if (Interlocked.Exchange(ref busy, 1) == 1)
				return;
			try
			{
				if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url))
				{
					LoadConfig();
					if (string.IsNullOrEmpty(key)) { LastError = "No key — create " + ConfigPath; return; }
				}
				List<Account> accts;
				lock (subscribed) accts = subscribed.Where(Online).ToList();
				var accounts = new List<Dictionary<string, object>>();
				foreach (Account a in accts)
				{
					var positions = new List<Dictionary<string, object>>();
					lock (a.Positions)
						foreach (Position p in a.Positions.Where(p => p.MarketPosition != MarketPosition.Flat))
							positions.Add(new Dictionary<string, object> {
								{ "contract", p.Instrument.FullName }, { "avg_price", p.AveragePrice },
								{ "qty", p.MarketPosition == MarketPosition.Long ? p.Quantity : -p.Quantity } });
					int working;
					lock (a.Orders) working = a.Orders.Count(o => o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted);
					accounts.Add(new Dictionary<string, object> {
						{ "name", a.Name }, { "broker", a.Provider.ToString() }, { "env", a.Connection != null ? a.Connection.Options.Name : "" },
						{ "balance", a.Get(AccountItem.CashValue, Currency.UsDollar) },
						{ "realized_pl", a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar) },
						{ "open_pl", a.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar) },
						{ "positions", positions }, { "working_orders", working } });
				}
				List<Dictionary<string, object>> fills, events;
				lock (pendingFills) { fills = pendingFills.Take(3000).ToList(); }
				lock (pendingEvents) { events = pendingEvents.ToList(); pendingEvents.Clear(); }
				var payload = new Dictionary<string, object> {
					{ "platform", "nt8" }, { "client", "nt8-" + Version }, { "accounts", accounts }, { "fills", fills }, { "events", events } };
				var content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
				var req = new HttpRequestMessage(HttpMethod.Post, url + "/api/ingest/state") { Content = content };
				req.Headers.Add("X-TG-Key", key);
				HttpResponseMessage res;
				try { res = http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult(); }
				catch (Exception ex)
				{
					lock (pendingEvents) pendingEvents.InsertRange(0, events);
					throw new Exception("server unreachable: " + ex.Message);
				}
				string body = res.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
				if (!res.IsSuccessStatusCode)
				{
					lock (pendingEvents) pendingEvents.InsertRange(0, events);
					throw new Exception("server " + (int)res.StatusCode + ": " + (body.Length > 160 ? body.Substring(0, 160) : body));
				}
				lock (pendingFills) pendingFills.RemoveRange(0, Math.Min(fills.Count, pendingFills.Count));
				Apply(json.Deserialize<Dictionary<string, object>>(body), accts);
				LastOk = DateTime.Now;
				LastError = "";
			}
			catch (Exception ex)
			{
				if (LastError != ex.Message) Log(ex.Message);
				LastError = ex.Message;
			}
			finally { Interlocked.Exchange(ref busy, 0); }
		}

		private static double? Num(object o)
		{
			if (o == null) return null;
			try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
		}

		private static void Apply(Dictionary<string, object> res, List<Account> accts)
		{
			object ao;
			if (res == null || !res.TryGetValue("accounts", out ao)) return;
			var map = ao as Dictionary<string, object>;
			if (map == null) return;
			foreach (var kv in map)
			{
				var d = kv.Value as Dictionary<string, object>;
				if (d == null) continue;
				var g = new GuardStatus { Name = kv.Key };
				g.Locked = d.ContainsKey("locked") && d["locked"] is bool && (bool)d["locked"];
				g.BlockEntries = d.ContainsKey("block_entries") && d["block_entries"] is bool && (bool)d["block_entries"];
				g.Flatten = d.ContainsKey("flatten") && d["flatten"] is bool && (bool)d["flatten"];
				g.Reason = d.ContainsKey("reason") ? Convert.ToString(d["reason"]) : "";
				g.FlattenReason = d.ContainsKey("flatten_reason") ? Convert.ToString(d["flatten_reason"]) : "";
				var r = d.ContainsKey("rules") ? d["rules"] as Dictionary<string, object> : null;
				if (r != null)
				{
					g.MaxDailyLoss = r.ContainsKey("max_daily_loss") ? Num(r["max_daily_loss"]) : null;
					g.MaxTradeLoss = r.ContainsKey("max_trade_loss") ? Num(r["max_trade_loss"]) : null;
					g.MaxContracts = r.ContainsKey("max_contracts") ? Num(r["max_contracts"]) : null;
				}
				var s = d.ContainsKey("stats") ? d["stats"] as Dictionary<string, object> : null;
				if (s != null)
				{
					g.DayPl = Num(s["day_pl"]) ?? 0; g.Realized = Num(s["realized"]) ?? 0; g.OpenPl = Num(s["open_pl"]) ?? 0;
					g.Trades = (int)(Num(s["trades"]) ?? 0);
				}
				var l = d.ContainsKey("lock") ? d["lock"] as Dictionary<string, object> : null;
				if (l != null) g.LockUntil = Num(l["until"]) ?? 0;
				lock (status) status[kv.Key] = g;
				if (g.Locked)
					lock (localLock) localLock.Remove(kv.Key);           // the server has taken over
			}
			foreach (Account a in accts)
			{
				GuardStatus g = Get(a);
				if (g == null) continue;
				if (g.Flatten)
					Flatten(a, string.IsNullOrEmpty(g.FlattenReason) ? g.Reason : g.FlattenReason);
				else if (g.BlockEntries)
				{
					bool entryWorking;
					lock (a.Orders) entryWorking = a.Orders.Any(o => (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) && !Reduces(a, o));
					if (entryWorking) Flatten(a, g.Reason, false);
				}
			}
		}

		public static List<GuardStatus> Snapshot()
		{
			lock (status) return status.Values.OrderByDescending(x => x.Locked).ThenBy(x => x.Name).ToList();
		}

		public static string ConnectedNames()
		{
			lock (subscribed) return string.Join(", ", subscribed.Where(Online).Select(a => a.Name));
		}
	}

	public class TiltguardWindow : NTWindow
	{
		private readonly StackPanel rows = new StackPanel { Margin = new Thickness(10) };
		private readonly TextBlock header = new TextBlock { Margin = new Thickness(10, 10, 10, 0), TextWrapping = TextWrapping.Wrap };
		private readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

		public TiltguardWindow()
		{
			Caption = "Tilt Guard";
			Width = 520;
			Height = 380;
			var root = new DockPanel();
			DockPanel.SetDock(header, Dock.Top);
			root.Children.Add(header);
			root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
			Content = root;
			tick.Tick += (s, e) => Refresh();
			tick.Start();
			Closed += (s, e) => tick.Stop();
			Refresh();
		}

		private static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

		private void Refresh()
		{
			bool ok = (DateTime.Now - TiltguardEngine.LastOk).TotalSeconds < 10;
			header.Text = "v" + TiltguardEngine.Version + " · " + (ok ? "connected" : "NOT connected: " + TiltguardEngine.LastError)
				+ "\nWatching: " + TiltguardEngine.ConnectedNames();
			header.Foreground = ok ? B("#2bd67b") : B("#f25672");
			rows.Children.Clear();
			foreach (GuardStatus g in TiltguardEngine.Snapshot())
			{
				string state = g.Locked ? "LOCKED until " + DateTimeOffset.FromUnixTimeSeconds((long)g.LockUntil).ToLocalTime().ToString("HH:mm")
					: g.BlockEntries ? "BLOCKED" : "OK";
				var line = new TextBlock
				{
					Text = string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,10:+$#,0;-$#,0;$0}  trades {2}  {3}", g.Name, g.DayPl, g.Trades, state)
						+ (g.BlockEntries && !string.IsNullOrEmpty(g.Reason) ? "\n    " + g.Reason : ""),
					FontFamily = new FontFamily("Consolas"),
					FontSize = 13,
					Margin = new Thickness(0, 3, 0, 3),
					Foreground = g.Locked ? B("#f25672") : g.BlockEntries ? B("#f5a524") : Application.Current.TryFindResource("FontLabelBrush") as Brush ?? Brushes.White,
				};
				rows.Children.Add(line);
			}
		}
	}
}
