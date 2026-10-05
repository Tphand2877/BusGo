using System.Globalization;
using System.Text;
using QRCoder;
using QuestPDF.Fluent;
using BusGo.Models;
using static BusGo.Services.UiText;

namespace BusGo.Services;

public sealed class TicketPrintModel
{
    public string TicketCode { get; init; } = "";
    public string OrderCode { get; init; } = "";
    public string StationName { get; init; } = "";
    public string StationAddress { get; init; } = "";
    public string Slogan { get; init; } = "An toàn - Nhanh chóng - Tiện lợi";
    public string PassengerName { get; init; } = "";
    public string PassengerPhone { get; init; } = "";
    public string PassengerType { get; init; } = "Người lớn";
    public string RouteName { get; init; } = "";
    public string RouteOrigin { get; init; } = "";
    public string RouteDestination { get; init; } = "";
    public string OriginStation { get; init; } = "";
    public string DestinationStation { get; init; } = "";
    public string DepartureDateFormatted { get; init; } = "";
    public string DepartureTimeFormatted { get; init; } = "";
    public string ArrivalTimeFormatted { get; init; } = "";
    public string SeatNumbers { get; init; } = "";
    public int SeatCount { get; init; } = 1;
    public string BusCompanyName { get; init; } = "";
    public string BusCompanyPhone { get; init; } = "";
    public string BusType { get; init; } = "";
    public string LicensePlate { get; init; } = "";
    public string DriverName { get; init; } = "";
    public decimal UnitPrice { get; init; }
    public decimal BaseFare { get; init; }
    public decimal ServiceFee { get; init; } = 0m;
    public decimal Discount { get; init; } = 0m;
    public decimal RefundAmount { get; init; } = 0m;
    public decimal TotalPrice { get; init; }
    public string PaymentMethod { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTime BookingTime { get; init; }
    public string IssuedAt { get; init; } = "";
    public string StaffCode { get; init; } = "";


    public static TicketPrintModel FromTicket(TicketDetails ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return new TicketPrintModel
        {
            TicketCode = ticket.TicketCode,
            OrderCode = ticket.TicketCode,
            StationName = "BusGo · " + ticket.Origin,
            StationAddress = ticket.Origin,
            PassengerName = ticket.PassengerName,
            PassengerPhone = ticket.PassengerPhone ?? "",
            RouteName = ticket.Route,
            RouteOrigin = ticket.Origin,
            RouteDestination = ticket.Destination,
            OriginStation = ticket.Origin,
            DestinationStation = ticket.Destination,
            DepartureDateFormatted = FormatVietnameseDate(ticket.DepartureTime.ToLocalTime()),
            DepartureTimeFormatted = ticket.DepartureTime.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            ArrivalTimeFormatted = ticket.ArrivalTime.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            SeatNumbers = ticket.SeatNumbers,
            SeatCount = ticket.SeatCount,
            BusCompanyName = ticket.CompanyName,
            BusCompanyPhone = ticket.CompanyHotline,
            BusType = ticket.BusType,
            LicensePlate = ticket.LicensePlate,
            DriverName = ticket.DriverName,
            UnitPrice = ticket.Price,
            BaseFare = ticket.BaseFare,
            Discount = ticket.DiscountAmount,
            RefundAmount = ticket.RefundAmount,
            TotalPrice = ticket.TotalPrice,
            PaymentMethod = ticket.PaymentMethod,
            Status = ticket.Status,
            BookingTime = ticket.BookingTime,
            IssuedAt = DateTime.Now.ToString("g", CultureInfo.CurrentCulture),
            StaffCode = "BusGo"
        };
    }

    public static string FormatVietnameseDate(DateTime date) =>
        date.ToString("d", CultureInfo.CurrentCulture) + " (" + date.ToString("dddd", CultureInfo.CurrentCulture) + ")";
}

internal static class Barcode128
{
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131",
        "211412", // 103 Start A
        "211214", // 104 Start B
        "211232", // 105 Start C
        "2331112" // 106 Stop
    ];

    public static bool[] Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) text = "TICKET";
        var sequence = new List<int> { 104 }; // Start B
        int checksum = 104;
        for (int i = 0; i < text.Length; i++)
        {
            int val = text[i] - 32;
            if (val < 0 || val > 95) val = 0;
            sequence.Add(val);
            checksum += val * (i + 1);
        }
        sequence.Add(checksum % 103);
        sequence.Add(106); // Stop

        var bits = new List<bool>();
        for (int q = 0; q < 8; q++) bits.Add(false);

        foreach (var code in sequence)
        {
            string pattern = Patterns[code];
            bool bar = true;
            foreach (char c in pattern)
            {
                int width = c - '0';
                for (int w = 0; w < width; w++) bits.Add(bar);
                bar = !bar;
            }
        }
        bits.Add(true);
        bits.Add(true);
        for (int q = 0; q < 8; q++) bits.Add(false);
        return bits.ToArray();
    }

    public static string GenerateSvg(string text)
    {
        var bits = Encode(text);
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {bits.Length} 40\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
        for (var x = 0; x < bits.Length; x++)
            if (bits[x]) svg.Append($"<rect x=\"{x}\" y=\"0\" width=\"1\" height=\"40\"/>");
        return svg.Append("</svg>").ToString();
    }
}

public static class TicketPrintService
{
    static TicketPrintService() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    public static byte[] GeneratePdf(TicketDetails ticket) => ExportToPdf(TicketPrintModel.FromTicket(ticket));
    public static byte[] GeneratePng(TicketDetails ticket) => CreateDocument(TicketPrintModel.FromTicket(ticket))
        .GenerateImages(new QuestPDF.Infrastructure.ImageGenerationSettings { ImageFormat = QuestPDF.Infrastructure.ImageFormat.Png }).Single();
    public static byte[] GenerateQrCodePng(string ticketCode) => GenerateQrCodePngBytes(ticketCode);
    public static byte[] ExportToPdf(TicketPrintModel model) => CreateDocument(model).GeneratePdf();
    public static void ExportToPdf(TicketPrintModel model, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        CreateDocument(model).GeneratePdf(filePath);
    }

    public static byte[] GenerateQrCodePngBytes(string ticketCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketCode);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(ticketCode, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(20);
    }

    private static QuestPDF.Infrastructure.IDocument CreateDocument(TicketPrintModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var qrBytes = GenerateQrCodePngBytes(model.TicketCode);
        var barcodeSvg = Barcode128.GenerateSvg(model.TicketCode);

        // Aspect ratio matches standard thermal ticket receipt ~400 x 720 points
        var receiptPageSize = new QuestPDF.Helpers.PageSize(410, 730);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(receiptPageSize);
            page.Margin(20);
            page.DefaultTextStyle(style => style.FontSize(9).FontColor("#0F172A"));
            page.Content().Column(column =>
            {
                column.Spacing(8);

                // 1. Header: Station Brand on Left, Barcode on Right
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(headerCol =>
                    {
                        headerCol.Item().Text(model.StationName).FontSize(14).Bold().FontColor("#0F4C81");
                        headerCol.Item().Text(Value(model.Slogan)).FontSize(8.5f).FontColor("#475569");
                        headerCol.Item().PaddingTop(2).Text(model.StationAddress).FontSize(7.5f).FontColor("#334155");
                    });

                    row.ConstantItem(150).Column(barCol =>
                    {
                        barCol.Item().Height(28).Svg(barcodeSvg).FitArea();
                        barCol.Item().AlignCenter().Text(Format("Mã vé: {0}", "Ticket code: {0}", model.TicketCode)).FontSize(8).Bold().FontColor("#0F172A");
                    });
                });

                // Divider line
                column.Item().LineHorizontal(1).LineColor("#CBD5E1");

                // 2. Title & Order Badge
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(titleCol =>
                    {
                        titleCol.Item().Text(T("VÉ XE KHÁCH", "BUS TICKET")).FontSize(20).Bold().FontColor("#0F4C81");
                        titleCol.Item().Text(Value(model.Status)).FontSize(9.5f).FontColor("#334155");
                    });

                    row.AutoItem().Background("#E0EDF8").PaddingVertical(6).PaddingHorizontal(14).Column(badgeCol =>
                    {
                        badgeCol.Item().AlignCenter().Text(T("Mã đơn", "Order code")).FontSize(8).FontColor("#475569");
                        badgeCol.Item().AlignCenter().Text(model.OrderCode).FontSize(10.5f).Bold().FontColor("#0F172A");
                    });
                });

                // 3. Quick Trip Info Strip (3 columns)
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(T("Ngày đi", "Travel date")).FontSize(8.5f).FontColor("#64748B");
                        col.Item().Text(model.DepartureDateFormatted).FontSize(10).Bold().FontColor("#0F172A");
                    });

                    row.ConstantItem(75).Column(col =>
                    {
                        col.Item().Text(T("Giờ đi", "Departure")).FontSize(8.5f).FontColor("#64748B");
                        col.Item().Text(model.DepartureTimeFormatted).FontSize(12).Bold().FontColor("#0F172A");
                    });

                    row.ConstantItem(85).Column(col =>
                    {
                        col.Item().Text(T("Ghế", "Seats")).FontSize(8.5f).FontColor("#64748B");
                        col.Item().Text(model.SeatNumbers).FontSize(12).Bold().FontColor("#0F172A");
                    });
                });

                // 4. Section 1: Passenger Information
                column.Item().Background("#F0F4F9").Padding(10).Column(box =>
                {
                    box.Spacing(5);
                    box.Item().Text(T("THÔNG TIN HÀNH KHÁCH", "PASSENGER INFORMATION")).FontSize(9.5f).Bold().FontColor("#0F4C81");
                    box.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(T("Họ tên", "Full name")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(model.PassengerName).FontSize(10).Bold().FontColor("#0F172A");
                            c.Item().PaddingTop(3).Text(T("SĐT", "Phone")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(model.PassengerPhone).FontSize(10).Bold().FontColor("#0F172A");
                        });

                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(T("Loại khách", "Passenger type")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(Value(model.PassengerType)).FontSize(10).Bold().FontColor("#0F172A");
                            c.Item().PaddingTop(3).Text(T("Số vé", "Ticket count")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(model.SeatCount.ToString()).FontSize(10).Bold().FontColor("#0F172A");
                        });
                    });
                });

                // 5. Center QR Code
                column.Item().AlignCenter().Column(qrCol =>
                {
                    qrCol.Item().AlignCenter().Width(115).Height(115).Image(qrBytes).FitArea();
                    qrCol.Item().AlignCenter().PaddingTop(3).Text(T("Quét mã QR để kiểm tra vé hoặc lên xe", "Scan the QR code to verify your ticket or board")).FontSize(8.5f).FontColor("#334155");
                });

                // 6. Section 2: Bus Operator Information
                column.Item().Background("#F0F4F9").Padding(10).Column(box =>
                {
                    box.Spacing(5);
                    box.Item().Text(T("THÔNG TIN NHÀ XE", "BUS OPERATOR INFORMATION")).FontSize(9.5f).Bold().FontColor("#0F4C81");
                    box.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(T("Nhà xe", "Bus operator")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(model.BusCompanyName).FontSize(10).Bold().FontColor("#0F172A");
                            c.Item().PaddingTop(3).Text(T("SĐT nhà xe", "Operator phone")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(model.BusCompanyPhone).FontSize(10).Bold().FontColor("#0F172A");
                        });

                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(T("Biển số", "License plate")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(model.LicensePlate).FontSize(10).Bold().FontColor("#0F172A");
                            c.Item().PaddingTop(3).Text(T("Tài xế", "Driver")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(Value(model.DriverName)).FontSize(10).Bold().FontColor("#0F172A");
                        });
                    });
                });

                // 7. Section 3: Route
                column.Item().Column(routeCol =>
                {
                    routeCol.Item().Text(T("TUYẾN ĐƯỜNG", "ROUTE")).FontSize(9.5f).Bold().FontColor("#0F4C81");
                    routeCol.Item().PaddingTop(1).Text($"{model.RouteOrigin}  →  {model.RouteDestination}").FontSize(14).Bold().FontColor("#0F4C81");
                    routeCol.Item().Text(Format("Bến đi: {0}   |   Bến đến: {1}", "From station: {0}   |   To station: {1}", model.OriginStation, model.DestinationStation)).FontSize(8).FontColor("#475569");
                    routeCol.Item().PaddingTop(5).LineHorizontal(1).LineColor("#E2E8F0");
                });

                // 8. Section 4: Payment Breakdown
                column.Item().Column(payCol =>
                {
                    payCol.Item().Text(T("THÔNG TIN THANH TOÁN", "PAYMENT INFORMATION")).FontSize(9.5f).Bold().FontColor("#0F4C81");
                    payCol.Item().PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem(1.3f).Column(c =>
                        {
                            c.Item().Row(r => { r.RelativeItem().Text(T("Giá vé", "Fare")).FontSize(8.5f).FontColor("#334155"); r.AutoItem().Text(Format("{0:N0}đ", "{0:N0} VND", model.BaseFare)).FontSize(8.5f).FontColor("#334155"); });
                            c.Item().Row(r => { r.RelativeItem().Text(T("Phí dịch vụ", "Service fee")).FontSize(8.5f).FontColor("#334155"); r.AutoItem().Text(Format("{0:N0}đ", "{0:N0} VND", model.ServiceFee)).FontSize(8.5f).FontColor("#334155"); });
                            c.Item().Row(r => { r.RelativeItem().Text(T("Giảm giá", "Discount")).FontSize(8.5f).FontColor("#334155"); r.AutoItem().Text(Format("{0:N0}đ", "{0:N0} VND", model.Discount)).FontSize(8.5f).FontColor("#334155"); });
                            c.Item().PaddingTop(5).Text(T("Phương thức thanh toán", "Payment method")).FontSize(7.5f).FontColor("#64748B");
                            c.Item().Text(Value(model.PaymentMethod)).FontSize(9.5f).Bold().FontColor("#0F172A");
                        });

                        row.ConstantItem(12).AlignCenter().LineVertical(1).LineColor("#E2E8F0");

                        row.RelativeItem(1.0f).Column(c =>
                        {
                            c.Item().Row(r => { r.RelativeItem().Text(T("Hoàn tiền", "Refund")).FontSize(8.5f).FontColor("#334155"); r.AutoItem().Text(Format("{0:N0}đ", "{0:N0} VND", model.RefundAmount)).FontSize(8.5f).FontColor("#334155"); });
                            c.Item().PaddingTop(10).Text(T("Tổng cộng", "Total")).FontSize(10).Bold().FontColor("#0F4C81");
                            c.Item().Text(Format("{0:N0}đ", "{0:N0} VND", model.TotalPrice)).FontSize(17).Bold().FontColor("#0F4C81");
                        });
                    });
                });

                // 9. Footer
                column.Item().LineHorizontal(1).LineColor("#CBD5E1");
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(T("Cảm ơn quý khách!", "Thank you!")).FontSize(9.5f).Bold().Italic().FontColor("#0F4C81");
                        c.Item().Text(T("Chúc quý khách thượng lộ bình an!", "Have a safe journey!")).FontSize(8.5f).Italic().FontColor("#475569");
                    });

                    row.AutoItem().Column(c =>
                    {
                        c.Item().AlignRight().Text(Format("In lúc: {0}", "Printed: {0}", model.IssuedAt)).FontSize(7.5f).FontColor("#64748B");
                        c.Item().AlignRight().Text(Format("NV: {0}", "Staff: {0}", model.StaffCode)).FontSize(7.5f).FontColor("#64748B");
                    });
                });
            });
        }));
    }

}
