using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace StoryRest.Stats
{
    /// <summary>
    /// 관람 한 건. "관람" 의 정의는 ArUcoViewCounter 가 정한다 — 여기서는 받은 것을 적기만 한다.
    /// </summary>
    public readonly struct ViewRecord
    {
        public readonly DateTime startedAt;   // 마커가 처음 잡힌 시각(로컬)
        public readonly int floor;
        public readonly string set;
        public readonly int markerId;
        public readonly float seconds;        // 처음 잡힌 때부터 마지막으로 보인 때까지

        public ViewRecord(DateTime startedAt, int floor, string set, int markerId, float seconds)
        {
            this.startedAt = startedAt;
            this.floor = floor;
            this.set = set;
            this.markerId = markerId;
            this.seconds = seconds;
        }
    }

    /// <summary>
    /// 관람 기록을 날짜별 CSV 로 남긴다. persistentDataPath/stats/views_YYYY-MM-DD.csv, 한 건에 한 줄.
    ///
    /// 집계(오늘의 순위, 일주일간 순위, 추천 …)는 아직 무엇을 보여 줄지 정해지지 않았다.
    /// 그래서 집계 결과가 아니라 **건별 원본**을 남긴다 — 기준이 나중에 바뀌어도 이 파일에서 다시 셀 수 있다.
    /// 하루에 수천 건이 쌓여도 몇백 KB 라 실행 중에 읽어 집계하는 데 무리가 없다.
    ///
    /// 한 줄씩 append 만 한다. 전시장 PC 는 예고 없이 꺼지므로, 파일을 통째로 다시 쓰는 방식이면
    /// 그 순간 지난 기록이 전부 날아갈 수 있다. append 는 최악의 경우 마지막 한 줄만 잃는다.
    ///
    /// 월 PC(role=wall)에도 같은 폴더가 있다 — ArUco PC 에서 받아 적은 **미러**다(→ ViewStatsClient).
    /// 그래서 연출 쪽은 어느 역할이든 ReadDay 와 Recorded 만 보면 되고, 파일이 어디서 왔는지 몰라도 된다.
    /// </summary>
    public static class ViewLog
    {
        public const string DirectoryName = "stats";
        public const string FilePrefix = "views_";
        public const string Header = "started_at,floor,set,marker,seconds";

        /// <summary>
        /// 한 건이 파일에 적힐 때마다(Append 성공 시) 메인 스레드에서 발생한다.
        /// ArUco PC 에서는 서버가 이것을 월 PC 로 보내고, 월 PC 에서는 받아 적을 때 같은 이벤트가 난다.
        /// 순위 화면 · 월 연출은 이것을 구독하면 파일을 다시 읽지 않고도 바로 따라갈 수 있다.
        /// </summary>
        public static event Action<ViewRecord> Recorded;

        /// <summary>
        /// 어느 날의 파일이 통째로 바뀌었을 때(WriteDay 성공 시) 메인 스레드에서 발생한다.
        /// 월 PC 가 ArUco PC 와 다시 맞출 때 나며, 그 날을 집계해 둔 쪽은 다시 읽어야 한다.
        /// </summary>
        public static event Action<DateTime> DayRewritten;

        public static string Directory => Path.Combine(Application.persistentDataPath, DirectoryName);

        /// <summary>그 날의 기록 파일. 날짜는 관람이 시작된 시각(로컬) 기준이다.</summary>
        public static string FileFor(DateTime day)
        {
            return Path.Combine(Directory, $"{FilePrefix}{day:yyyy-MM-dd}.csv");
        }

        public static bool Append(in ViewRecord record)
        {
            string path = FileFor(record.startedAt);

            try
            {
                if (!System.IO.Directory.Exists(Directory)) System.IO.Directory.CreateDirectory(Directory);

                bool isNew = !File.Exists(path);

                using (var writer = new StreamWriter(path, append: true, new System.Text.UTF8Encoding(false)))
                {
                    if (isNew) writer.WriteLine(Header);
                    writer.WriteLine(Format(record));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Stats] 관람 기록을 쓰지 못했습니다: {path}\n{e.Message}");
                return false;
            }

            Recorded?.Invoke(record);
            return true;
        }

        /// <summary>
        /// 그 날의 파일을 받은 줄들로 통째로 바꾼다. 월 PC 가 ArUco PC 의 파일을 미러할 때 쓴다.
        /// 원본은 append 만 하지만 미러는 원본을 그대로 베끼는 것이라 통째로 써도 잃을 것이 없다.
        /// 임시 파일에 쓴 뒤 바꿔치기해서, 쓰다 꺼져도 반쯤 쓰인 파일이 남지 않게 한다.
        /// </summary>
        public static bool WriteDay(DateTime day, IReadOnlyList<string> lines)
        {
            string path = FileFor(day);
            string temp = path + ".tmp";

            try
            {
                if (!System.IO.Directory.Exists(Directory)) System.IO.Directory.CreateDirectory(Directory);

                using (var writer = new StreamWriter(temp, append: false, new System.Text.UTF8Encoding(false)))
                {
                    writer.WriteLine(Header);
                    for (int i = 0; i < lines.Count; i++) writer.WriteLine(lines[i]);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Stats] 관람 기록 미러를 쓰지 못했습니다: " + path + " — " + e.Message);
                return false;
            }

            DayRewritten?.Invoke(day.Date);
            return true;
        }

        /// <summary>그 날의 줄들을 파싱하지 않고 그대로 담는다(헤더 제외). 네트워크로 넘길 때 쓴다.</summary>
        public static void ReadDayLines(DateTime day, List<string> into)
        {
            string path = FileFor(day);
            if (!File.Exists(path)) return;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Stats] 관람 기록을 읽지 못했습니다: " + path + " — " + e.Message);
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0 && lines[i].StartsWith("started_at")) continue;
                if (lines[i].Length > 0) into.Add(lines[i]);
            }
        }

        /// <summary>
        /// 그 날의 기록을 전부 읽어 목록에 더한다. 파일이 없으면 아무것도 하지 않는다.
        /// 깨진 줄(쓰다 전원이 끊긴 마지막 줄 등)은 건너뛴다.
        /// </summary>
        public static void ReadDay(DateTime day, List<ViewRecord> into)
        {
            string path = FileFor(day);
            if (!File.Exists(path)) return;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Stats] 관람 기록을 읽지 못했습니다: {path}\n{e.Message}");
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0 && lines[i].StartsWith("started_at")) continue;
                if (TryParse(lines[i], out var record)) into.Add(record);
            }
        }

        public static bool TryParse(string line, out ViewRecord record)
        {
            record = default;

            var parts = line.Split(',');
            if (parts.Length < 5) return false;

            var inv = CultureInfo.InvariantCulture;
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", inv, DateTimeStyles.None, out var startedAt)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, inv, out int floor)) return false;
            if (!int.TryParse(parts[3], NumberStyles.Integer, inv, out int markerId)) return false;
            if (!float.TryParse(parts[4], NumberStyles.Float, inv, out float seconds)) return false;

            record = new ViewRecord(startedAt, floor, parts[2], markerId, seconds);
            return true;
        }

        // 엑셀과 스크립트 양쪽에서 그대로 읽히도록 ISO 시각 · 소수점은 마침표로 고정한다.
        public static string Format(in ViewRecord r)
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join(",",
                r.startedAt.ToString("yyyy-MM-ddTHH:mm:ss", inv),
                r.floor.ToString(inv),
                r.set,
                r.markerId.ToString(inv),
                r.seconds.ToString("0.0", inv));
        }
    }
}
