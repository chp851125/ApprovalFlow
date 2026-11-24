using System.Text.Json;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Infrastructure.Persistence;

namespace ApprovalFlow.Infrastructure.Seeding;

/// <summary>
/// 範例資料產生器（僅限 Development 環境執行）。
/// 使用固定亂數種子，重複執行產生相同內容（密碼雜湊的鹽值除外）。
/// 執行方式：<c>dotnet run -- --generate-seed [--anchor yyyy-MM-dd]</c>
/// 這是唯一包含範例內容的程式碼；系統執行期只讀取 JSON。
/// </summary>
public sealed partial class MockDataGenerator
{
    private const int RandomSeed = 20261123;
    private const string DemoPassword = "Demo@1234";
    private const string MailDomain = "calculus.example";
    private const int DefaultSla = 16;
    private static readonly TimeSpan Tz = TimeSpan.FromHours(8);

    private readonly Random _rng = new(RandomSeed);
    private readonly IPasswordService _passwords;
    private readonly DateOnly _anchor;
    private readonly DateTimeOffset _now;

    private readonly List<Holiday> _holidays = [];
    private readonly List<JobTitle> _titles = [];
    private readonly List<Department> _departments = [];
    private readonly List<Employee> _employees = [];
    private readonly List<LeaveType> _leaveTypes = [];
    private readonly List<LeaveBalance> _balances = [];
    private readonly List<BudgetSubject> _budgets = [];
    private readonly List<ExpenseCategory> _categories = [];
    private readonly List<Supplier> _suppliers = [];
    private readonly List<SystemSetting> _settings = [];
    private readonly List<DocumentTypeConfig> _typeConfigs = [];
    private readonly List<DocumentSequence> _sequences = [];
    private readonly List<WorkflowTemplate> _templates = [];
    private readonly List<Delegation> _delegations = [];
    private readonly List<Document> _documents = [];
    private readonly List<Notification> _notifications = [];
    private readonly List<AuditLog> _audits = [];

    private readonly Dictionary<string, Employee> _acc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Department> _deptByCode = [];
    private WorkCalendar _cal = null!;
    private OrgSnapshot _org = null!;

    public MockDataGenerator(IPasswordService passwords, DateOnly anchor)
    {
        _passwords = passwords;
        _anchor = anchor;
        _now = At(anchor, 9, 30);
    }

    public string Summary { get; private set; } = "";

    public void WriteTo(string folder)
    {
        Generate();
        Directory.CreateDirectory(folder);
        Write(folder, _departments);
        Write(folder, _titles);
        Write(folder, _employees);
        Write(folder, _delegations);
        Write(folder, _leaveTypes);
        Write(folder, _balances);
        Write(folder, _categories);
        Write(folder, _budgets);
        Write(folder, _suppliers);
        Write(folder, _holidays);
        Write(folder, _settings);
        Write(folder, _typeConfigs);
        Write(folder, _sequences);
        Write(folder, _templates);
        Write(folder, _documents);
        Write(folder, new List<Attachment>());
        Write(folder, _notifications);
        Write(folder, new List<EmailMessage>());
        Write(folder, _audits);

        Summary = $"部門 {_departments.Count}、職稱 {_titles.Count}、員工 {_employees.Count}、假別 {_leaveTypes.Count}、" +
                  $"費用類別 {_categories.Count}、預算科目 {_budgets.Count}、供應商 {_suppliers.Count}、假日 {_holidays.Count}、" +
                  $"流程範本 {_templates.Count}、單據 {_documents.Count}（" +
                  string.Join("、", _documents.GroupBy(d => d.Status).OrderBy(g => g.Key).Select(g => $"{g.Key.ToDisplay()} {g.Count()}")) +
                  $"）、代理 {_delegations.Count}、通知 {_notifications.Count}、稽核 {_audits.Count}";
    }

    private static void Write<T>(string folder, List<T> items)
    {
        var path = Path.Combine(folder, JsonDefaults.FileNameFor(typeof(T)));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(items, JsonDefaults.Options));
    }

    private void Generate()
    {
        GenerateHolidays();
        GenerateOrganization();
        _cal = new WorkCalendar(_holidays);
        _org = new OrgSnapshot(_employees, _departments);
        GenerateLeaveData();
        GenerateFinanceData();
        GenerateSettings();
        GenerateTemplates();
        GenerateDelegations();
        GenerateDocuments();
        GenerateNotifications();
        GenerateAuditLogs();
    }

    // ------------------------------------------------------------------ helpers

    private static DateTimeOffset At(DateOnly d, int h, int m = 0) => new(d.ToDateTime(new TimeOnly(h, m)), Tz);
    private int R(int min, int maxInclusive) => _rng.Next(min, maxInclusive + 1);
    private double RD(double min, double max) => min + _rng.NextDouble() * (max - min);
    private bool Chance(double p) => _rng.NextDouble() < p;
    private T Pick<T>(IReadOnlyList<T> list) => list[_rng.Next(list.Count)];
    private Employee E(string account) => _acc[account];

    private T Weighted<T>(params (T Item, int Weight)[] items)
    {
        var total = items.Sum(i => i.Weight);
        var roll = _rng.Next(total);
        foreach (var (item, w) in items)
        {
            if (roll < w) return item;
            roll -= w;
        }
        return items[^1].Item;
    }

    private static decimal Round10(double v) => Math.Round((decimal)v / 10m, MidpointRounding.AwayFromZero) * 10m;

    // ------------------------------------------------------------------ 假日

    private void GenerateHolidays()
    {
        // 依行政院人事行政總處公告之 115 年（2026）政府行政機關辦公日曆表。
        // 2025 年《紀念日及節日實施條例》施行後，115 年度不再安排補行上班日，因此本年度無補班日資料；
        // 系統仍支援 MakeupWorkday 類型，管理員可於假日行事曆自行新增。
        (string Date, string Name)[] list =
        [
            ("2026-01-01", "中華民國開國紀念日"),
            ("2026-02-16", "農曆除夕"),
            ("2026-02-17", "春節"),
            ("2026-02-18", "春節"),
            ("2026-02-19", "春節"),
            ("2026-02-20", "春節補假（小年夜逢週日）"),
            ("2026-02-27", "和平紀念日補假"),
            ("2026-04-03", "兒童節補假"),
            ("2026-04-06", "民族掃墓節補假"),
            ("2026-05-01", "勞動節"),
            ("2026-06-19", "端午節"),
            ("2026-09-25", "中秋節"),
            ("2026-09-28", "教師節"),
            ("2026-10-09", "國慶日補假"),
            ("2026-10-26", "臺灣光復暨金門古寧頭大捷紀念日補假"),
            ("2026-12-25", "行憲紀念日"),
        ];
        var id = 1;
        foreach (var (date, name) in list)
            _holidays.Add(new Holiday { Id = id++, Version = 1, Date = DateOnly.Parse(date), Name = name, Type = HolidayType.Holiday });
    }

    // ------------------------------------------------------------------ 組織

    private sealed record EmpSpec(string Account, string Dept, string Title, string? Manager, string[] Roles, string Hire, string? Resigned = null);

    private void GenerateOrganization()
    {
        (string Code, string Name, int Rank)[] titles =
        [
            ("T01", "總經理", 90), ("T02", "處長", 70), ("T03", "經理", 60),
            ("T04", "組長", 40), ("T05", "專員", 20), ("T06", "助理", 10)
        ];
        var tid = 1;
        foreach (var (code, name, rank) in titles)
            _titles.Add(new JobTitle { Id = tid, Version = 1, Code = code, Name = name, Rank = rank, SortOrder = tid++ });

        (string Code, string Name, string? Parent)[] depts =
        [
            ("GMO", "總經理室", null),
            ("ADM", "管理處", "GMO"),
            ("FIN", "財務會計部", "ADM"),
            ("SAL", "業務部", "GMO")
        ];
        var did = 1;
        foreach (var (code, name, parent) in depts)
        {
            var d = new Department
            {
                Id = did, Version = 1, Code = code, Name = name, SortOrder = did++,
                ParentId = parent is null ? null : _deptByCode[parent].Id
            };
            _departments.Add(d);
            _deptByCode[code] = d;
        }

        EmpSpec[] specs =
        [
            new("gm", "GMO", "T01", null, [Roles.Manager, Roles.DeptManager], "2012-03-01"),
            new("gm.asst", "GMO", "T05", "gm", [], "2019-08-12"),
            new("adm.head", "ADM", "T02", "gm", [Roles.Manager, Roles.DeptManager], "2014-06-03"),
            new("hr.lead", "ADM", "T04", "adm.head", [Roles.Manager, Roles.HR], "2016-09-19"),
            new("hr01", "ADM", "T05", "hr.lead", [Roles.HR], "2021-04-06"),
            new("hr02", "ADM", "T05", "hr.lead", [Roles.HR], "2022-02-14", "2026-05-29"),
            new("admin", "ADM", "T05", "adm.head", [Roles.Admin], "2018-11-01"),
            new("ga01", "ADM", "T06", "adm.head", [], "2023-07-03"),
            new("fin.mgr", "FIN", "T03", "adm.head", [Roles.Manager, Roles.DeptManager, Roles.Finance], "2015-01-05"),
            new("acc01", "FIN", "T05", "fin.mgr", [Roles.Finance], "2017-05-15"),
            new("acc02", "FIN", "T05", "fin.mgr", [Roles.Finance], "2020-10-05"),
            new("acc03", "FIN", "T06", "fin.mgr", [Roles.Finance], "2024-03-11"),
            new("sales.mgr", "SAL", "T03", "gm", [Roles.Manager, Roles.DeptManager], "2013-09-02"),
            new("sales.lead1", "SAL", "T04", "sales.mgr", [Roles.Manager], "2016-02-22"),
            new("sales.lead2", "SAL", "T04", "sales.mgr", [Roles.Manager], "2018-07-09"),
            new("sales01", "SAL", "T05", "sales.lead1", [], "2019-03-18"),
            new("sales02", "SAL", "T05", "sales.lead1", [], "2021-08-02"),
            new("sales03", "SAL", "T05", "sales.lead2", [], "2022-05-16"),
            new("sales04", "SAL", "T06", "sales.lead2", [], "2024-09-02"),
            new("sales05", "SAL", "T05", "sales.lead1", [], "2020-06-01", "2026-03-31"),
            new("sales06", "SAL", "T05", "sales.lead2", [], "2025-01-06"),
        ];

        string[] surnames = ["陳", "林", "黃", "張", "李", "王", "吳", "劉", "蔡", "楊", "許", "鄭", "謝", "郭", "洪", "曾", "邱", "廖", "賴", "周", "徐", "蘇", "葉", "莊", "呂", "江", "何", "蕭", "羅", "高"];
        string[] given = ["志明", "淑芬", "俊傑", "雅婷", "家豪", "怡君", "冠宇", "佩珊", "建宏", "美玲", "宗翰", "欣怡", "承恩", "詩涵", "柏翰", "宜蓁", "彥廷", "惠雯", "信宏", "筱涵", "文彬", "珮瑜", "哲維", "郁婷", "明哲", "雅雯", "子軒", "思妤", "國華", "靜宜"];
        var usedNames = new HashSet<string>();
        var hash = _passwords.Hash(DemoPassword);

        var eid = 1;
        foreach (var s in specs)
        {
            string name;
            do name = Pick(surnames) + Pick(given); while (!usedNames.Add(name));

            var emp = new Employee
            {
                Id = eid,
                Version = 1,
                EmployeeNo = $"HY{eid:000}",
                Account = s.Account,
                Name = name,
                Email = $"{s.Account}@{MailDomain}",
                Phone = $"09{R(10, 89)}-{R(100, 999)}-{R(100, 999)}",
                Extension = $"{(_deptByCode[s.Dept].Id * 100) + eid}",
                DepartmentId = _deptByCode[s.Dept].Id,
                JobTitleId = _titles.First(t => t.Code == s.Title).Id,
                ManagerId = s.Manager is null ? null : _acc[s.Manager].Id,
                Roles = [Roles.Applicant, .. s.Roles],
                HireDate = DateOnly.Parse(s.Hire),
                ResignedDate = s.Resigned is null ? null : DateOnly.Parse(s.Resigned),
                IsActive = s.Resigned is null,
                PasswordHash = hash,
                SecurityStamp = $"{RandomSeed:x}{eid:x4}{_rng.Next():x8}",
                PasswordChangedAt = At(DateOnly.Parse(s.Hire) < new DateOnly(2026, 1, 5) ? new DateOnly(2026, 1, 5) : DateOnly.Parse(s.Hire), 10, R(0, 59))
            };
            _employees.Add(emp);
            _acc[s.Account] = emp;
            eid++;
        }

        _deptByCode["GMO"].ManagerId = E("gm").Id;
        _deptByCode["ADM"].ManagerId = E("adm.head").Id;
        _deptByCode["FIN"].ManagerId = E("fin.mgr").Id;
        _deptByCode["SAL"].ManagerId = E("sales.mgr").Id;
    }

    // ------------------------------------------------------------------ 假別與額度

    private void GenerateLeaveData()
    {
        _leaveTypes.AddRange(
        [
            new LeaveType { Id = 1, Version = 1, Code = "ANNUAL", Name = "特別休假", UseSeniorityQuota = true, PayRatio = 1, MinUnitHours = 1, SortOrder = 1, Description = "依勞基法第 38 條依年資給假" },
            new LeaveType { Id = 2, Version = 1, Code = "PERSONAL", Name = "事假", DefaultQuotaHours = 112, PayRatio = 0, MinUnitHours = 1, SortOrder = 2, Description = "一年內合計不得超過 14 日，不給薪" },
            new LeaveType { Id = 3, Version = 1, Code = "SICK", Name = "病假", DefaultQuotaHours = 240, PayRatio = 0.5m, MinUnitHours = 1, SortOrder = 3, Description = "未住院者一年內合計不得超過 30 日，半薪；連續 3 日以上須檢附證明" },
            new LeaveType { Id = 4, Version = 1, Code = "MARRIAGE", Name = "婚假", DefaultQuotaHours = 64, PayRatio = 1, MinUnitHours = 4, RequiresAttachment = true, SortOrder = 4, Description = "結婚者給予 8 日，須檢附結婚證明" },
            new LeaveType { Id = 5, Version = 1, Code = "FUNERAL", Name = "喪假", DefaultQuotaHours = 64, PayRatio = 1, MinUnitHours = 4, RequiresAttachment = true, SortOrder = 5, Description = "依親等給予 3 至 8 日，須檢附訃聞" },
            new LeaveType { Id = 6, Version = 1, Code = "OFFICIAL", Name = "公假", PayRatio = 1, MinUnitHours = 1, RequiresAttachment = true, SortOrder = 6, Description = "兵役召集、出庭作證、公務受訓等，須檢附通知文件" },
        ]);

        var bid = 1;
        foreach (var e in _employees)
        {
            foreach (var lt in _leaveTypes.Where(l => l.UseSeniorityQuota || l.DefaultQuotaHours.HasValue))
            {
                var hours = lt.UseSeniorityQuota ? AnnualLeaveDays(e.HireDate, new DateOnly(2026, 1, 1)) * 8m : lt.DefaultQuotaHours!.Value;
                _balances.Add(new LeaveBalance { Id = bid++, Version = 1, EmployeeId = e.Id, LeaveTypeId = lt.Id, Year = 2026, EntitledHours = hours });
            }
        }
    }

    /// <summary>勞基法第 38 條特休日數</summary>
    private static int AnnualLeaveDays(DateOnly hire, DateOnly asOf)
    {
        var months = (asOf.Year - hire.Year) * 12 + asOf.Month - hire.Month;
        var years = months / 12.0;
        if (years < 0.5) return 0;
        if (years < 1) return 3;
        if (years < 2) return 7;
        if (years < 3) return 10;
        if (years < 5) return 14;
        if (years < 10) return 15;
        return Math.Min(30, 15 + (int)years - 9);
    }

    // ------------------------------------------------------------------ 財務主檔

    private void GenerateFinanceData()
    {
        (string Code, string Name, decimal Budget, string Owner)[] budgets =
        [
            ("6111", "旅費", 600_000, "SAL"),
            ("6112", "交際費", 360_000, "GMO"),
            ("6113", "文具印刷費", 120_000, "ADM"),
            ("6114", "郵電費", 150_000, "ADM"),
            ("6115", "職工訓練費", 200_000, "ADM"),
            ("6116", "設備及雜項購置", 1_200_000, "ADM"),
        ];
        var id = 1;
        foreach (var (code, name, budget, owner) in budgets)
            _budgets.Add(new BudgetSubject { Id = id++, Version = 1, Code = code, Name = name, Year = 2026, AnnualBudget = budget, OwnerDepartmentId = _deptByCode[owner].Id });

        (string Code, string Name, string Budget, decimal? Max, bool Receipt, string Desc)[] cats =
        [
            ("TRAFFIC", "交通費", "6111", 5_000, true, "高鐵、計程車、停車費、油資等"),
            ("LODGING", "差旅住宿", "6111", 4_000, true, "出差住宿，每晚上限 4,000 元"),
            ("MEAL", "誤餐費", "6111", 500, false, "加班或出差誤餐，每餐上限 500 元"),
            ("ENTERTAIN", "交際費", "6112", 20_000, true, "客戶餐敘、禮品，須註明對象"),
            ("STATIONERY", "文具用品", "6113", null, true, "辦公文具、紙張"),
            ("POSTAGE", "郵電費", "6114", null, true, "郵資、快遞、電話費"),
            ("TRAINING", "教育訓練", "6115", 30_000, true, "外部課程、研討會報名費"),
            ("MISC", "雜項支出", "6116", 10_000, true, "其他零星支出"),
        ];
        id = 1;
        foreach (var c in cats)
            _categories.Add(new ExpenseCategory
            {
                Id = id, Version = 1, Code = c.Code, Name = c.Name, BudgetSubjectId = _budgets.First(b => b.Code == c.Budget).Id,
                MaxAmountPerItem = c.Max, RequiresReceipt = c.Receipt, Description = c.Desc, SortOrder = id++
            });

        (string Name, string Category, string Contact, string City)[] sups =
        [
            ("晨曦資訊科技股份有限公司", "資訊設備", "許先生", "臺北市內湖區瑞光路"),
            ("鼎昇辦公家具有限公司", "辦公家具", "林小姐", "新北市五股區五權路"),
            ("恆裕文具事務用品社", "文具耗材", "黃老闆", "臺北市中山區長安東路"),
            ("澄遠網路通訊股份有限公司", "網路通訊", "陳經理", "新北市汐止區新台五路"),
            ("栢林機電工程有限公司", "機電工程", "王先生", "桃園市龜山區文化一路"),
            ("青禾餐飲事業有限公司", "餐飲", "蔡小姐", "臺北市大安區復興南路"),
            ("宇匯印刷有限公司", "印刷", "張先生", "新北市中和區中正路"),
            ("昱成電腦耗材有限公司", "電腦耗材", "李小姐", "臺北市松山區八德路"),
        ];
        id = 1;
        foreach (var s in sups)
        {
            _suppliers.Add(new Supplier
            {
                Id = id, Version = 1, Code = $"S{id:000}", Name = s.Name, Category = s.Category,
                TaxId = $"{R(2, 9)}{R(1000000, 9999999)}",
                ContactName = s.Contact, Phone = $"02-{R(2200, 2999)}-{R(1000, 9999)}",
                Email = $"sales{id:00}@supplier{id:00}.example",
                Address = $"{s.City}{R(1, 300)}號{R(1, 12)}樓"
            });
            id++;
        }
    }

    // ------------------------------------------------------------------ 系統參數與單別

    private void GenerateSettings()
    {
        (string Key, string Value, string Group, string Type, string Desc)[] list =
        [
            (SettingKeys.CompanyName, "微積分科技股份有限公司", "一般", "string", "公司名稱，顯示於頁首與 Email"),
            (SettingKeys.SiteBaseUrl, "https://localhost:7016", "一般", "string", "系統網址，用於 Email 中的連結"),
            (SettingKeys.PasswordMinLength, "8", "密碼原則", "int", "密碼最短長度"),
            (SettingKeys.PasswordRequireUpper, "true", "密碼原則", "bool", "需包含大寫英文字母"),
            (SettingKeys.PasswordRequireLower, "true", "密碼原則", "bool", "需包含小寫英文字母"),
            (SettingKeys.PasswordRequireDigit, "true", "密碼原則", "bool", "需包含數字"),
            (SettingKeys.PasswordRequireSymbol, "false", "密碼原則", "bool", "需包含符號"),
            (SettingKeys.LockoutThreshold, "5", "登入安全", "int", "連續登入失敗幾次後鎖定"),
            (SettingKeys.LockoutMinutes, "15", "登入安全", "int", "鎖定分鐘數"),
            (SettingKeys.UploadMaxSizeMb, "10", "附件", "int", "單一附件大小上限（MB）"),
            (SettingKeys.UploadAllowedExtensions, ".pdf,.jpg,.jpeg,.png,.xlsx,.docx", "附件", "string", "允許的副檔名（逗號分隔）"),
            (SettingKeys.UploadMaxFilesPerDocument, "10", "附件", "int", "每張單據附件數上限"),
            (SettingKeys.BatchApproveEnabled, "true", "簽核", "bool", "是否開放批次核准（僅限設定為低風險的單別）"),
            (SettingKeys.SlaDefaultHours, DefaultSla.ToString(), "時效", "int", "關卡未設定時效時的預設工作時數"),
            (SettingKeys.SlaReminderBeforeHours, "4", "時效", "int", "到期前幾個工作小時發送提醒"),
            (SettingKeys.SlaAutoEscalate, "true", "時效", "bool", "逾時後是否自動升級給上一層主管"),
            (SettingKeys.SlaEscalateAfterHours, "8", "時效", "int", "逾時超過幾個工作小時後自動升級"),
            (SettingKeys.DigestEnabled, "true", "通知", "bool", "是否寄送每日待辦摘要"),
            (SettingKeys.DigestHour, "8", "通知", "int", "每日待辦摘要寄送時間（時）"),
            (SettingKeys.EmailEnabled, "true", "通知", "bool", "是否寄送 Email（模擬寫入 outbox）"),
            (SettingKeys.EmailFromAddress, $"approval@{MailDomain}", "通知", "string", "寄件者地址"),
        ];
        var id = 1;
        foreach (var s in list)
            _settings.Add(new SystemSetting { Id = id++, Version = 1, Key = s.Key, Value = s.Value, Group = s.Group, ValueType = s.Type, Description = s.Desc });

        _typeConfigs.AddRange(
        [
            new DocumentTypeConfig { Id = 1, Version = 1, DocumentType = DocumentType.Leave, Name = "請假單", Prefix = "LV", AllowBatchApprove = true, Icon = "bi-calendar2-week", Description = "各類假別申請，送出時檢查假別餘額" },
            new DocumentTypeConfig { Id = 2, Version = 1, DocumentType = DocumentType.Expense, Name = "費用報銷單", Prefix = "EX", AllowBatchApprove = false, Icon = "bi-receipt", Description = "差旅、交際、雜支等費用報銷" },
            new DocumentTypeConfig { Id = 3, Version = 1, DocumentType = DocumentType.Purchase, Name = "採購申請單", Prefix = "PR", AllowBatchApprove = false, Icon = "bi-cart3", Description = "設備、耗材與服務採購" },
            new DocumentTypeConfig { Id = 4, Version = 1, DocumentType = DocumentType.Overtime, Name = "加班申請單", Prefix = "OT", AllowBatchApprove = true, Icon = "bi-clock-history", Description = "平日或假日加班事前 / 事後申請" },
        ]);
    }

    // ------------------------------------------------------------------ 流程範本

    private void GenerateTemplates()
    {
        var created = At(new DateOnly(2025, 12, 15), 10);
        var adminId = E("admin").Id;

        WorkflowStepDefinition Step(int no, string name, ApproverSource src, ApprovalMode mode = ApprovalMode.Single,
            StepCondition? cond = null, int? sla = null, string? role = null, List<int>? users = null, int levels = 1) =>
            new() { StepNo = no, Name = name, ApproverSource = src, Mode = mode, Condition = cond, SlaHours = sla, RoleName = role, UserIds = users ?? [], Levels = levels };

        StepCondition Cond(ConditionField f, ConditionOperator op, decimal v) => new() { Field = f, Operator = op, Value = v };

        _templates.Add(new WorkflowTemplate
        {
            Id = 1, Version = 1, DocumentType = DocumentType.Leave, Name = "請假標準流程", TemplateVersion = 1, IsActive = true,
            EffectiveFrom = new DateOnly(2025, 1, 1), CreatedAt = created, CreatedById = adminId,
            Description = "直屬主管核准；超過 3 天加會部門主管；人資備查",
            Steps =
            [
                Step(1, "直屬主管核准", ApproverSource.DirectManager, sla: 16),
                Step(2, "部門主管核准", ApproverSource.DepartmentManager, cond: Cond(ConditionField.Days, ConditionOperator.GreaterThan, 3), sla: 16),
                Step(3, "人資備查", ApproverSource.Role, ApprovalMode.Any, role: Roles.HR, sla: 24),
            ]
        });
        _templates.Add(new WorkflowTemplate
        {
            Id = 2, Version = 1, DocumentType = DocumentType.Expense, Name = "費用報銷流程", TemplateVersion = 1, IsActive = false,
            EffectiveFrom = new DateOnly(2025, 1, 1), CreatedAt = created, CreatedById = adminId,
            Description = "（舊版）直屬主管核准後由財務審核",
            Steps =
            [
                Step(1, "直屬主管核准", ApproverSource.DirectManager, sla: 16),
                Step(2, "財務審核", ApproverSource.Role, ApprovalMode.Any, role: Roles.Finance, sla: 24),
            ]
        });
        _templates.Add(new WorkflowTemplate
        {
            Id = 3, Version = 1, DocumentType = DocumentType.Expense, Name = "費用報銷流程", TemplateVersion = 2, IsActive = true,
            EffectiveFrom = new DateOnly(2026, 6, 1), CreatedAt = At(new DateOnly(2026, 5, 25), 14, 20), CreatedById = adminId,
            Description = "2026/6 起：1 萬元以上加會部門主管，5 萬元以上需總經理核決",
            Steps =
            [
                Step(1, "直屬主管核准", ApproverSource.DirectManager, sla: 16),
                Step(2, "部門主管核准", ApproverSource.DepartmentManager, cond: Cond(ConditionField.Amount, ConditionOperator.GreaterThanOrEqual, 10_000), sla: 16),
                Step(3, "財務審核", ApproverSource.Role, ApprovalMode.Any, role: Roles.Finance, sla: 24),
                Step(4, "總經理核決", ApproverSource.SpecificUser, users: [E("gm").Id], cond: Cond(ConditionField.Amount, ConditionOperator.GreaterThanOrEqual, 50_000), sla: 24),
            ]
        });
        _templates.Add(new WorkflowTemplate
        {
            Id = 4, Version = 1, DocumentType = DocumentType.Purchase, Name = "採購申請流程", TemplateVersion = 1, IsActive = true,
            EffectiveFrom = new DateOnly(2025, 1, 1), CreatedAt = created, CreatedById = adminId,
            Description = "3 萬元以上需上兩級主管；財務經理與會計會簽；10 萬元以上總經理核決",
            Steps =
            [
                Step(1, "直屬主管核准", ApproverSource.DirectManager, sla: 16),
                Step(2, "上兩級主管核准", ApproverSource.ManagerLevelN, levels: 2, cond: Cond(ConditionField.Amount, ConditionOperator.GreaterThanOrEqual, 30_000), sla: 16),
                Step(3, "財務會簽", ApproverSource.SpecificUser, ApprovalMode.All, users: [E("fin.mgr").Id, E("acc01").Id], sla: 24),
                Step(4, "總經理核決", ApproverSource.SpecificUser, users: [E("gm").Id], cond: Cond(ConditionField.Amount, ConditionOperator.GreaterThanOrEqual, 100_000), sla: 24),
            ]
        });
        _templates.Add(new WorkflowTemplate
        {
            Id = 5, Version = 1, DocumentType = DocumentType.Overtime, Name = "加班申請流程", TemplateVersion = 1, IsActive = true,
            EffectiveFrom = new DateOnly(2025, 1, 1), CreatedAt = created, CreatedById = adminId,
            Description = "直屬主管核准後人資備查",
            Steps =
            [
                Step(1, "直屬主管核准", ApproverSource.DirectManager, sla: 16),
                Step(2, "人資備查", ApproverSource.Role, ApprovalMode.Any, role: Roles.HR, sla: 24),
            ]
        });
    }

    // ------------------------------------------------------------------ 代理人

    private void GenerateDelegations()
    {
        (string Principal, string Delegate, DateOnly Start, DateOnly End, string Reason)[] list =
        [
            ("fin.mgr", "acc01", new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 6), "參加會計師公會 IFRS 進修課程"),
            ("sales.mgr", "sales.lead1", new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 24), "特休出國"),
            ("sales.lead2", "sales.lead1", _anchor.AddDays(-3), _anchor.AddDays(4), "南部客戶巡迴拜訪出差"),
            ("adm.head", "hr.lead", _anchor.AddDays(10), _anchor.AddDays(14), "年度特休"),
        ];
        var id = 1;
        foreach (var d in list)
        {
            var principal = E(d.Principal);
            var createdOn = d.Start.AddDays(-R(2, 6));
            if (createdOn >= _anchor) createdOn = _anchor.AddDays(-R(1, 3));
            _delegations.Add(new Delegation
            {
                Id = id++, Version = 1, PrincipalId = principal.Id, DelegateId = E(d.Delegate).Id,
                StartDate = d.Start, EndDate = d.End, Reason = d.Reason, IsEnabled = true,
                CreatedById = principal.Id, CreatedAt = At(_cal.PreviousWorkday(createdOn), R(9, 17), R(0, 59))
            });
        }
    }
}
