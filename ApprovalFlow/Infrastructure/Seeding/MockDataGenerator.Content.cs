using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Infrastructure.Seeding;

public sealed partial class MockDataGenerator
{
    private void FillContent(Document doc, Employee app, DateOnly date, DocSpec spec)
    {
        switch (doc.DocumentType)
        {
            case DocumentType.Leave: FillLeave(doc, app, date, spec); break;
            case DocumentType.Expense: FillExpense(doc, app, date, spec); break;
            case DocumentType.Purchase: FillPurchase(doc, app, date, spec); break;
            case DocumentType.Overtime: FillOvertime(doc, app, date); break;
        }
    }

    private static string FormatHours(decimal hours) =>
        hours % 8 == 0 ? $"{hours / 8:0} 天" : $"{hours:0.#} 小時";

    // ------------------------------------------------------------------ 請假

    private void FillLeave(Document doc, Employee app, DateOnly date, DocSpec spec)
    {
        var code = spec.Special switch
        {
            "delegation" => "ANNUAL",
            "resubmit-sick" => "SICK",
            _ => Weighted(("ANNUAL", 45), ("PERSONAL", 20), ("SICK", 20), ("OFFICIAL", 7), ("MARRIAGE", 3), ("FUNERAL", 5))
        };
        var lt = _leaveTypes.First(l => l.Code == code);
        decimal hours = spec.Special switch
        {
            "delegation" => 8,
            "resubmit-sick" => 24,
            _ => code switch
            {
                "ANNUAL" => Pick(new decimal[] { 4, 8, 8, 8, 16, 24, 40 }),
                "PERSONAL" => Pick(new decimal[] { 2, 4, 4, 8 }),
                "SICK" => Pick(new decimal[] { 4, 8, 8, 16 }),
                "OFFICIAL" => Pick(new decimal[] { 8, 16 }),
                "MARRIAGE" => 64,
                _ => Pick(new decimal[] { 24, 40 })
            }
        };

        // 病假多為事後補請，其他假別為事前申請
        var start = code == "SICK"
            ? _cal.PreviousWorkday(date.AddDays(-R(0, 2)))
            : _cal.NextWorkday(date.AddDays(R(2, 20)));
        var days = (int)Math.Ceiling(hours / 8m);
        var end = _cal.AddWorkdays(start, days);

        DateTimeOffset startAt, endAt;
        if (hours >= 8) { startAt = At(start, 9); endAt = At(end, 18); }
        else if (hours == 4)
        {
            var morning = Chance(0.5);
            startAt = morning ? At(start, 9) : At(start, 14);
            endAt = morning ? At(start, 13) : At(start, 18);
        }
        else { startAt = At(start, 16); endAt = At(start, 18); }

        var deputy = _employees
            .Where(e => e.IsActive && e.DepartmentId == app.DepartmentId && e.Id != app.Id && e.Id != app.ManagerId)
            .OrderBy(_ => _rng.Next()).FirstOrDefault();

        var reason = Pick(code switch
        {
            "ANNUAL" => new[] { "家庭旅遊", "返鄉探親", "處理個人事務", "小孩學校活動", "年度特休安排", "出國旅遊" },
            "PERSONAL" => new[] { "至戶政事務所辦理證件", "家中水管維修需在家等候", "陪同家人就醫", "銀行貸款對保" },
            "SICK" => new[] { "感冒發燒就醫", "腸胃炎就診", "牙齒疼痛就醫", "偏頭痛就診", "身體不適在家休養" },
            "OFFICIAL" => new[] { "教育召集", "法院出庭作證", "參加政府機關舉辦之職安衛訓練" },
            "MARRIAGE" => new[] { "本人結婚" },
            _ => new[] { "祖父過世，返鄉治喪", "外婆過世，協助處理後事" }
        });

        doc.Content.Leave = new LeaveForm
        {
            LeaveTypeId = lt.Id, StartAt = startAt, EndAt = endAt, Hours = hours, Reason = reason, DeputyId = deputy?.Id
        };
        doc.Hours = hours;
        doc.Title = $"{lt.Name} {start:M/d}{(end != start ? $"–{end:M/d}" : "")}（{FormatHours(hours)}）";
        if (deputy != null) doc.CcEmployeeIds.Add(deputy.Id);
    }

    // ------------------------------------------------------------------ 費用

    private static readonly Dictionary<string, (string Desc, int Min, int Max)[]> ExpenseCatalog = new()
    {
        ["TRAFFIC"] =
        [
            ("高鐵 臺北—臺中 來回，拜訪客戶", 1_490, 1_490), ("高鐵 臺北—左營 來回，南區客戶會議", 2_980, 2_980),
            ("計程車 公司—客戶辦公室", 180, 520), ("停車費 客戶園區", 60, 200), ("油資 業務車出勤", 600, 1_800)
        ],
        ["LODGING"] = [("臺中出差住宿 1 晚", 2_200, 3_600), ("高雄出差住宿 1 晚", 2_400, 3_800), ("新竹出差住宿 1 晚", 2_000, 3_200)],
        ["MEAL"] = [("出差誤餐", 120, 300), ("加班誤餐", 100, 250)],
        ["ENTERTAIN"] = [("客戶餐敘（年度合約續約）", 3_500, 12_000), ("拜訪客戶伴手禮", 1_200, 3_000), ("經銷商季度會議餐敘", 4_000, 9_000)],
        ["STATIONERY"] = [("A4 影印紙 10 箱", 1_100, 1_400), ("白板筆、便利貼等文具", 300, 900), ("資料夾、名片印製", 600, 1_800)],
        ["POSTAGE"] = [("快遞合約正本至客戶", 150, 450), ("掛號郵資", 60, 200), ("國際快遞樣品", 900, 2_500)],
        ["TRAINING"] = [("個資法實務課程報名費", 3_500, 6_000), ("Excel 進階資料分析課程", 4_200, 5_800), ("勞動法規研習報名費", 2_500, 4_000)],
        ["MISC"] = [("辦公室盆栽維護", 800, 1_500), ("飲水機濾心更換", 1_200, 2_400), ("會議室延長線與轉接頭", 400, 1_200)],
    };

    private ExpenseItem NewExpenseItem(Employee app, DateOnly submit, string? forceCat = null)
    {
        var isSales = app.DepartmentId == _deptByCode["SAL"].Id;
        var cat = forceCat ?? (isSales
            ? Weighted(("TRAFFIC", 40), ("LODGING", 15), ("MEAL", 15), ("ENTERTAIN", 15), ("POSTAGE", 5), ("TRAINING", 5), ("STATIONERY", 5))
            : Weighted(("TRAFFIC", 20), ("STATIONERY", 20), ("POSTAGE", 15), ("TRAINING", 15), ("MISC", 15), ("MEAL", 15)));
        var (desc, min, max) = Pick(ExpenseCatalog[cat]);
        var category = _categories.First(c => c.Code == cat);
        return new ExpenseItem
        {
            Date = _cal.PreviousWorkday(submit.AddDays(-R(1, 25))),
            CategoryId = category.Id,
            Description = desc,
            Amount = min == max ? min : Round10(RD(min, max)),
            ReceiptNo = category.RequiresReceipt ? $"{(char)('A' + R(0, 25))}{(char)('A' + R(0, 25))}-{R(10000000, 99999999)}" : null
        };
    }

    private static void RecalcExpense(Document doc)
    {
        var f = doc.Content.Expense!;
        f.Items = f.Items.OrderBy(i => i.Date).ToList();
        f.TotalAmount = f.Items.Sum(i => i.Amount);
        doc.Amount = f.TotalAmount;
    }

    private void FillExpense(Document doc, Employee app, DateOnly date, DocSpec spec)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var count = spec.Special == "resubmit" ? R(2, 4) : R(1, 4);
            var items = Enumerable.Range(0, count).Select(_ => NewExpenseItem(app, date)).ToList();
            if (spec.Special == "resubmit") items[1] = NewExpenseItem(app, date, "TRAFFIC");
            if (spec.Special == "high-amount")
            {
                items.Add(NewExpenseItem(app, date, "ENTERTAIN"));
                items.Add(NewExpenseItem(app, date, "LODGING"));
                items.Add(NewExpenseItem(app, date, "LODGING"));
            }
            var total = items.Sum(i => i.Amount);
            var ok = spec.Special switch
            {
                "high-amount" => total is >= 10_000 and < 50_000,
                "transfer" => total < 10_000,
                _ => total < 50_000
            };
            if (!ok) continue;

            var isSales = app.DepartmentId == _deptByCode["SAL"].Id;
            var month = items.Min(i => i.Date).Month;
            var purpose = isSales
                ? Pick(new[] { "客戶拜訪差旅費", "南區經銷商巡迴拜訪", "新竹科學園區客戶簡報", "年度合約續約洽談", "展覽參展差旅" })
                : Pick(new[] { "部門行政雜支", "外部教育訓練費用", "辦公用品及郵電費", "公務外出交通費" });
            doc.Content.Expense = new ExpenseForm
            {
                Items = items,
                PaymentMethod = Weighted((PaymentMethod.BankTransfer, 75), (PaymentMethod.PettyCash, 20), (PaymentMethod.Cash, 5)),
                ProjectCode = isSales && Chance(0.5) ? $"PJ-2026-{R(1, 40):000}" : null,
                Purpose = purpose
            };
            RecalcExpense(doc);
            doc.Title = $"{month} 月 {purpose}";
            return;
        }
        throw new InvalidOperationException("無法產生符合條件的費用單");
    }

    // ------------------------------------------------------------------ 採購

    private static readonly (string Name, string Spec, string Unit, int Min, int Max, string Supplier, string Budget)[] PurchaseCatalog =
    [
        ("筆記型電腦", "14 吋 / Core i7 / 16GB / 512GB SSD", "台", 36_000, 42_000, "S001", "6116"),
        ("27 吋液晶顯示器", "IPS / QHD / 含 Type-C", "台", 7_200, 9_800, "S001", "6116"),
        ("人體工學辦公椅", "網背 / 可調頭枕扶手", "張", 6_800, 9_900, "S002", "6116"),
        ("雷射多功能事務機", "黑白 A4 / 雙面列印 / 網路", "台", 14_500, 18_900, "S001", "6116"),
        ("24 埠網路交換器", "Gigabit / 網管型", "台", 11_000, 14_000, "S004", "6116"),
        ("會議室視訊鏡頭", "4K / 自動追焦 / 含全向麥克風", "組", 18_000, 26_000, "S004", "6116"),
        ("UPS 不斷電系統", "1.5KVA 在線式", "台", 9_500, 13_000, "S005", "6116"),
        ("碳粉匣", "原廠高容量", "支", 2_400, 3_200, "S008", "6113"),
        ("A4 影印紙", "80 磅 / 5 包裝", "箱", 520, 650, "S003", "6113"),
        ("鋼製檔案櫃", "四層抽屜附鎖", "座", 4_200, 5_800, "S002", "6116"),
        ("公司簡介型錄印製", "A4 / 16 頁 / 銅版紙", "本", 45, 80, "S007", "6113"),
        ("外接式硬碟", "2TB / USB 3.2", "顆", 2_300, 2_900, "S008", "6116"),
    ];

    private void FillPurchase(Document doc, Employee app, DateOnly date, DocSpec spec)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var count = R(1, 3);
            var chosen = PurchaseCatalog.OrderBy(_ => _rng.Next()).Take(count).ToList();
            var items = chosen.Select(c =>
            {
                var qty = c.Unit switch { "本" => R(200, 500), "箱" => R(5, 20), "支" => R(2, 8), _ => R(1, 4) };
                var price = Round10(RD(c.Min, c.Max));
                return new PurchaseItem { Name = c.Name, Specification = c.Spec, Quantity = qty, Unit = c.Unit, UnitPrice = price, Amount = qty * price };
            }).ToList();
            var total = items.Sum(i => i.Amount);
            var ok = spec.Special switch
            {
                "addsigner" or "return-prev" => total is >= 30_000 and < 100_000,
                "high-amount" => total >= 100_000 && total < 250_000,
                _ => total < 180_000
            };
            if (!ok) continue;

            var main = chosen[0];
            var dept = _departments.First(d => d.Id == app.DepartmentId);
            doc.Content.Purchase = new PurchaseForm
            {
                SupplierId = _suppliers.First(s => s.Code == main.Supplier).Id,
                BudgetSubjectId = _budgets.First(b => b.Code == main.Budget).Id,
                Items = items,
                TotalAmount = total,
                RequiredDate = _cal.NextWorkday(date.AddDays(R(7, 30))),
                Purpose = Pick(new[]
                {
                    "現有設備使用逾五年，效能不足影響日常作業。", "配合新進同仁到職，需配置作業設備。",
                    "視訊會議需求增加，提升遠端會議品質。", "年度耗材補充，避免缺料影響作業。",
                    "配合辦公室座位調整，新增相關設備。"
                }),
                DeliveryLocation = $"臺北總部 {R(7, 9)}F {dept.Name}"
            };
            doc.Amount = total;
            doc.Title = $"{dept.Name} {main.Name}採購" + (items.Count > 1 ? $"等 {items.Count} 項" : "");
            var ga = E("ga01");
            if (app.Id != ga.Id) doc.CcEmployeeIds.Add(ga.Id);
            return;
        }
        throw new InvalidOperationException("無法產生符合條件的採購單");
    }

    // ------------------------------------------------------------------ 加班

    private void FillOvertime(Document doc, Employee app, DateOnly date)
    {
        var holiday = Chance(0.2);
        DateOnly otDate;
        if (holiday)
        {
            otDate = date.AddDays(R(1, 6));
            while (_cal.IsWorkday(otDate)) otDate = otDate.AddDays(1);
        }
        else
        {
            otDate = Chance(0.7) ? date : _cal.NextWorkday(date.AddDays(1));
        }

        decimal hours;
        TimeOnly start, end;
        if (holiday)
        {
            hours = Pick(new decimal[] { 4, 8 });
            start = new TimeOnly(9, 0);
            end = hours == 4 ? new TimeOnly(13, 0) : new TimeOnly(18, 0);
        }
        else
        {
            hours = Pick(new decimal[] { 1.5m, 2, 2, 3, 3.5m });
            start = new TimeOnly(18, 30);
            end = start.AddMinutes((int)(hours * 60));
        }

        var dept = _departments.First(d => d.Id == app.DepartmentId).Code;
        var reason = Pick(dept switch
        {
            "FIN" => new[] { "月結關帳作業", "年度預算編列", "營業稅申報資料整理", "會計師查核資料準備" },
            "SAL" => new[] { "客戶專案提案簡報準備", "年度展覽佈展", "配合客戶系統上線現場支援", "季度業績報表彙整" },
            "ADM" => new[] { "機房設備維護（需停機作業）", "年度資產盤點", "新人報到資料準備", "辦公室搬遷協調" },
            _ => new[] { "董事會資料準備", "主管會議簡報整理" }
        });

        doc.Content.Overtime = new OvertimeForm
        {
            Date = otDate,
            StartTime = start,
            EndTime = end,
            Hours = hours,
            Compensation = Weighted((OvertimeCompensation.Pay, 60), (OvertimeCompensation.CompensatoryLeave, 40)),
            Reason = reason,
            IsHoliday = holiday
        };
        doc.Hours = hours;
        doc.Title = $"{(holiday ? "假日" : "平日")}加班 {otDate:M/d}（{hours:0.#} 小時）{reason}";
    }
}
