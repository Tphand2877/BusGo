using System.Text;
using static BusGo.Services.UiText;
using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;
namespace BusGo.Pages.Admin;

public sealed class ReportsModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public DateTime From { get; set; } = DateTime.UtcNow.Date.AddDays(-29);
    [BindProperty(SupportsGet = true)] public DateTime To { get; set; } = DateTime.UtcNow.Date;
    public IReadOnlyList<RevenueReportItem> Rows { get; private set; } = [];
    public IReadOnlyList<CompanyRevenueReportItem> CompanyRows { get; private set; } = [];
    public IReadOnlyList<PaymentChannelReportItem> ChannelRows { get; private set; } = [];

    public int Tickets => Rows.Sum(r => r.TicketsSold);
    public decimal Gross => Rows.Sum(r => r.GrossRevenue);
    public decimal Refunds => Rows.Sum(r => r.RefundedAmount);
    public decimal Net => Rows.Sum(r => r.NetRevenue);
    public decimal Commission => Rows.Sum(r => r.CommissionAmount);
    public decimal Payout => Rows.Sum(r => r.CompanyPayout);

    public decimal AverageFare => Tickets > 0 ? Math.Round(Net / Tickets) : 0m;
    public decimal AverageCommissionRate => Net > 0 ? Math.Round((Commission / Net) * 100m, 1) : 10m;
    public string TopRoute => Rows.OrderByDescending(r => r.NetRevenue).FirstOrDefault()?.RouteSummary ?? "-";
    public string TopCompany => CompanyRows.OrderByDescending(c => c.NetRevenue).FirstOrDefault()?.CompanyName ?? "-";

    public async Task OnGetAsync(int? days)
    {
        if (days is 7 or 30) { To = DateTime.UtcNow.Date; From = To.AddDays(1 - days.Value); }
        if (!ModelState.IsValid) return;
        if (To.Date < From.Date || To.Date == DateTime.MaxValue.Date)
        {
            ModelState.AddModelError(string.Empty, T("Chọn khoảng ngày hợp lệ với ngày kết thúc không trước ngày bắt đầu.", "Choose a valid date range with the end on or after the start."));
            return;
        }
        await LoadSafely(async () =>
        {
            var from = From.Date;
            var to = To.Date.AddDays(1);
            Rows = await AdminDatabaseService.GetRevenueReportAsync(from, to);
            CompanyRows = await AdminDatabaseService.GetCompanyRevenueReportAsync(from, to);
            ChannelRows = await AdminDatabaseService.GetPaymentChannelReportAsync(from, to);
        });
    }

    public async Task<IActionResult> OnGetExportExcelAsync(int? days)
    {
        if (days is 7 or 30) { To = DateTime.UtcNow.Date; From = To.AddDays(1 - days.Value); }
        if (To.Date < From.Date || To.Date == DateTime.MaxValue.Date)
        {
            TempData["Error"] = T("Khoảng ngày không hợp lệ để xuất báo cáo.", "Invalid date range for report export.");
            return RedirectToPage(new { From = From.ToString("yyyy-MM-dd"), To = To.ToString("yyyy-MM-dd") });
        }

        var from = From.Date;
        var to = To.Date.AddDays(1);
        var routeRows = await AdminDatabaseService.GetRevenueReportAsync(from, to);
        var companyRows = await AdminDatabaseService.GetCompanyRevenueReportAsync(from, to);
        var channelRows = await AdminDatabaseService.GetPaymentChannelReportAsync(from, to);

        var tickets = routeRows.Sum(r => r.TicketsSold);
        var gross = routeRows.Sum(r => r.GrossRevenue);
        var refunds = routeRows.Sum(r => r.RefundedAmount);
        var net = routeRows.Sum(r => r.NetRevenue);
        var commission = routeRows.Sum(r => r.CommissionAmount);
        var payout = routeRows.Sum(r => r.CompanyPayout);
        var avgFare = tickets > 0 ? Math.Round(net / tickets) : 0m;
        var avgCommRate = net > 0 ? Math.Round((commission / net) * 100m, 1) : 10m;

        static string CsvEscape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        var sb = new StringBuilder();

        sb.AppendLine("BÁO CÁO DOANH THU & HOA HỒNG BẾN XE BUSGO");
        sb.AppendLine($"Kỳ báo cáo: Từ {From:dd/MM/yyyy} đến {To:dd/MM/yyyy} (UTC)");
        sb.AppendLine($"Bến xe: {CsvEscape(Station)}");
        sb.AppendLine($"Thời gian xuất file: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Người lập báo cáo: {CsvEscape(CurrentUser.Account?.FullName ?? "Administrator")} ({CsvEscape(CurrentUser.Account?.Role ?? "Admin")})");
        sb.AppendLine();

        sb.AppendLine("I. TỔNG HỢP CHỈ SỐ TÀI CHÍNH");
        sb.AppendLine("Chỉ số,Giá trị");
        sb.AppendLine($"Tổng số vé bán,{tickets}");
        sb.AppendLine($"Tổng doanh thu gộp (VND),{gross:F0}");
        sb.AppendLine($"Tổng tiền hoàn lại (VND),{refunds:F0}");
        sb.AppendLine($"Doanh thu thuần (VND),{net:F0}");
        sb.AppendLine($"Hoa hồng bến xe (VND),{commission:F0}");
        sb.AppendLine($"Chi trả cho nhà xe đối tác (VND),{payout:F0}");
        sb.AppendLine($"Giá vé bình quân (VND),{avgFare:F0}");
        sb.AppendLine($"Tỷ lệ hoa hồng bình quân,{avgCommRate}%");
        sb.AppendLine();

        sb.AppendLine("II. CHI TIẾT DOANH THU THEO TUYẾN XE");
        sb.AppendLine("STT,Tuyến xe,Số vé bán,Doanh thu gộp (VND),Hoàn tiền (VND),Doanh thu thuần (VND),Tỷ lệ hoa hồng (%),Tiền hoa hồng (VND),Chi trả nhà xe (VND)");
        for (int i = 0; i < routeRows.Count; i++)
        {
            var r = routeRows[i];
            sb.AppendLine($"{i + 1},{CsvEscape(r.RouteSummary)},{r.TicketsSold},{r.GrossRevenue:F0},{r.RefundedAmount:F0},{r.NetRevenue:F0},{r.CommissionRate * 100:0.#}%,{r.CommissionAmount:F0},{r.CompanyPayout:F0}");
        }
        sb.AppendLine($"Tổng cộng,,{tickets},{gross:F0},{refunds:F0},{net:F0},,{commission:F0},{payout:F0}");
        sb.AppendLine();

        sb.AppendLine("III. CHI TIẾT THEO NHÀ XE / ĐỐI TÁC VẬN HÀNH");
        sb.AppendLine("STT,Tên nhà xe / Đối tác,Số chuyến chạy,Số vé bán,Doanh thu gộp (VND),Hoàn tiền (VND),Doanh thu thuần (VND),Tỷ lệ hoa hồng (%),Hoa hồng bến (VND),Chi trả nhà xe (VND)");
        for (int i = 0; i < companyRows.Count; i++)
        {
            var c = companyRows[i];
            sb.AppendLine($"{i + 1},{CsvEscape(c.CompanyName)},{c.TripsCount},{c.TicketsSold},{c.GrossRevenue:F0},{c.RefundedAmount:F0},{c.NetRevenue:F0},{c.CommissionRate * 100:0.#}%,{c.CommissionAmount:F0},{c.CompanyPayout:F0}");
        }
        sb.AppendLine($"Tổng cộng,,{companyRows.Sum(c => c.TripsCount)},{companyRows.Sum(c => c.TicketsSold)},{companyRows.Sum(c => c.GrossRevenue):F0},{companyRows.Sum(c => c.RefundedAmount):F0},{companyRows.Sum(c => c.NetRevenue):F0},,{companyRows.Sum(c => c.CommissionAmount):F0},{companyRows.Sum(c => c.CompanyPayout):F0}");
        sb.AppendLine();

        sb.AppendLine("IV. PHÂN BỔ THEO KÊNH THANH TOÁN & ĐỐI SOÁT");
        sb.AppendLine("Kênh thanh toán,Số giao dịch thành công,Số tiền thu (VND),Số giao dịch hoàn tiền,Số tiền hoàn (VND),Doanh thu thuần (VND),Tỷ trọng (%)");
        foreach (var ch in channelRows)
        {
            var pct = net > 0 ? Math.Round((ch.NetAmount / net) * 100m, 1) : 0m;
            sb.AppendLine($"{CsvEscape(ch.ChannelDisplay)},{ch.SuccessCount},{ch.SuccessAmount:F0},{ch.RefundedCount},{ch.RefundedAmount:F0},{ch.NetAmount:F0},{pct}%");
        }

        var fileName = $"BaoCao_DoanhThu_BusGo_{From:yyyyMMdd}_{To:yyyyMMdd}.csv";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }
}
