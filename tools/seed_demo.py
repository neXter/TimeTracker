"""
Developer tool: fills a TimeTracker database with ~3 weeks of realistic sample usage so the dashboard and reports
can be previewed. NEVER run this against your real database; point it at a scratch copy:

    set TIMETRACKER_DATA=C:\\temp\\tt-demo
    TimeTracker.exe --dev          (start once so the database exists, then exit it)
    python tools/seed_demo.py C:\\temp\\tt-demo\\tracker.db
"""
import datetime as dt
import random
import sqlite3
import sys

path = sys.argv[1]
rnd = random.Random(42)
con = sqlite3.connect(path)
now = int(dt.datetime.now().timestamp() * 1000)

APPS = [  # key, exe path, detected name, source, title_rule_id, weight (share of foreground time)
    ("mc", r"C:\Program Files\Java\bin\javaw.exe", "Minecraft", "alias", 1, 30),
    ("ch", r"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", "version-info", 0, 22),
    ("dc", r"C:\Users\Demo\AppData\Local\Discord\Discord.exe", "Discord", "version-info", 0, 12),
    ("wd", r"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", "Microsoft Word", "version-info", 0, 6),
    ("rl", r"D:\Steam\steamapps\common\rocketleague\Binaries\Win64\RocketLeague.exe", "Rocket League", "steam", 0, 14),
    ("st", r"C:\Program Files (x86)\Steam\steam.exe", "Steam", "version-info", 0, 3),
    ("on", r"C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE", "Microsoft OneNote", "version-info", 0, 4),
    ("sp", r"C:\Users\Demo\AppData\Roaming\Spotify\Spotify.exe", "Spotify", "version-info", 0, 2),
    ("gx", r"D:\Games\HollowQuest\Binaries\Win64\game_x64.exe", "game_x64.exe", "fallback", 0, 8),
]
ids = {}
for key, p, name, src, rule, _ in APPS:
    exe = p.split("\\")[-1]
    cur = con.execute("INSERT OR IGNORE INTO app(exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc) VALUES(?,?,?,?,?,?)",
                      (p, exe, name, src, rule, now - 30 * 86400000))
    ids[key] = con.execute("SELECT id FROM app WHERE exe_path=? AND title_rule_id=?", (p, rule)).fetchone()[0]


def ms(day, hour):
    h = int(hour); m = int(round((hour - h) * 60))
    return int(dt.datetime.combine(day, dt.time(h, m)).timestamp() * 1000)


def pick(favour):
    pool = [(k, w * (3 if k in favour else 1)) for k, _, _, _, _, w in APPS]
    total = sum(w for _, w in pool)
    r = rnd.uniform(0, total)
    for k, w in pool:
        r -= w
        if r <= 0:
            return k
    return pool[0][0]


today = dt.date.today()
for back in range(21, -1, -1):
    day = today - dt.timedelta(days=back)
    weekend = day.weekday() >= 5
    sessions = ([(9.5, 12.5), (14.0, 17.5), (19.0, 22.5)] if weekend else
                [(7.1, 7.7), (15.5, 17.6), (18.6, 21.2)])
    sessions = [(a + rnd.uniform(-0.3, 0.3), b + rnd.uniform(-0.4, 0.4)) for a, b in sessions if rnd.random() > (0.35 if weekend else 0.12)]
    last_end = None
    for a, b in sessions:
        s, e = ms(day, a), ms(day, b)
        if s >= now - 60000:
            continue
        e = min(e, now - 90000)
        if e - s < 600000:
            continue
        if last_end:
            con.execute("INSERT INTO device_interval(state,start_utc,end_utc) VALUES('locked',?,?)", (last_end, s))
        last_end = e
        favour = {"mc", "rl", "dc"} if (weekend or a > 15) else {"wd", "on", "ch"}
        t = s
        open_since = {}
        while t < e:
            if rnd.random() < 0.07:      # short idle break
                d = rnd.randint(6, 12) * 60000
                d = min(d, e - t)
                con.execute("INSERT INTO device_interval(state,start_utc,end_utc) VALUES('idle',?,?)", (t, t + d))
                t += d
                continue
            d = min(rnd.randint(3, 28) * 60000, e - t)
            k = pick(favour)
            con.execute("INSERT INTO device_interval(state,start_utc,end_utc) VALUES('active',?,?)", (t, t + d))
            con.execute("INSERT INTO foreground_interval(app_id,start_utc,end_utc) VALUES(?,?,?)", (ids[k], t, t + d))
            open_since.setdefault(k, t)
            t += d
        for k, since in open_since.items():
            end = e if k in ("ch", "dc", "st", "sp") else min(e, since + rnd.randint(40, 150) * 60000)
            start = since - (rnd.randint(0, 20) * 60000 if k in ("ch", "dc", "sp") else 0)
            con.execute("INSERT INTO open_interval(app_id,start_utc,end_utc) VALUES(?,?,?)", (ids[k], max(start, s), end))
        for k in ("dc", "sp"):   # always running in the background
            if k not in open_since and rnd.random() < 0.7:
                con.execute("INSERT INTO open_interval(app_id,start_utc,end_utc) VALUES(?,?,?)", (ids[k], s, e))
con.commit()
print("seeded", con.execute("select count(*) from foreground_interval").fetchone()[0], "foreground intervals")
