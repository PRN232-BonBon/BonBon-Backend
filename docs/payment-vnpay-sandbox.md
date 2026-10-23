# Giai đoạn 2C — Backend Payment / VNPay sandbox

## Phạm vi và trạng thái

Thanh toán toàn bộ tiền thuê Booking đã duyệt, VND nguyên. Không cọc, Wallet,
phụ phí, chia tiền chủ xe, hoàn tiền hoặc giao dịch production. Không đổi cách
tính giá hoặc API Booking Approval. Không tạo database test và không kết nối
database nhóm trong giai đoạn này.

Payment.Enabled mặc định false trong cả options, appsettings và Compose.
Chỉ URL gateway chính xác https://sandbox.vnpayment.vn/paymentv2/vpcpay.html
được chấp nhận. Chưa có merchant sandbox thực tế trong source.

**Chưa thể vận hành trên schema hiện tại của nhóm.** Repository yêu cầu proposal
`database/proposals/004_vnpay_sandbox.sql` được review, áp dụng riêng sau này và
kiểm thử trên môi trường được cho phép. File chưa được thực thi, không được mount
vào init database, không có migration hay bootstrap lúc application startup.
Thiếu marker/cột/index => PAYMENT_SCHEMA_UNAVAILABLE (HTTP 503 hoặc IPN 99).
Không dùng in-memory idempotency hoặc bỏ qua kiểm tra schema để lách giới hạn này.

## Thành phần

PaymentController -> IPaymentService/PaymentService -> IPaymentRepository/
PaymentRepository. IPaymentGateway/VnPayGateway chịu trách nhiệm ký và xác minh.
PaymentNotificationPolicy là policy thuần, được chạy bởi service trong callback
của repository khi đã khóa dữ liệu. Các phép ghi/audit/confirm thuộc cùng một
transaction. Dùng Npgsql và BonBonDbContext hiện có; không đăng ký context scaffold
thứ hai, không scaffold lại entity payment hoặc sửa database đang dùng chung.

## API

| Endpoint | Quyền / hành vi |
| --- | --- |
| POST /api/payments | JWT Customer; body {bookingId}; header Idempotency-Key 16–100 ký tự chữ/số/_/- |
| GET /api/payments/{paymentId} | JWT Customer; chỉ thanh toán của khách hiện tại và Customer role Active |
| GET /api/payments/vnpay/ipn | Anonymous, bắt buộc xác minh VNPay; cập nhật transaction |
| GET /api/payments/vnpay/return | Anonymous, xác minh callback, đọc trạng thái; không có đường ghi |
| GET /api/payments/vnpay/return/redirect | Anonymous, cùng xác minh chỉ đọc; HTTP 302 về Frontend cố định từ cấu hình |

Create trả HTTP 200 gồm paymentId, bookingId, amount, currency, status,
checkoutUrl, expiresAt, isReplay. Body không nhận amount/status/customer/owner.
Idempotency-Key có phạm vi một Booking. Cùng key trả attempt cũ khi Booking còn
payable; attempt Failed/Cancelled không trả lại URL để thanh toán. Key khác trong
khi có attempt đang mở trả PAYMENT_IN_PROGRESS. Booking đã tiến khỏi
AwaitingPayment không tạo thêm thanh toán (409). Trước đó không có guarantee rằng
POST replay sau Confirmed sẽ trả lại checkout: dùng GET trạng thái thay vì tạo lại.

GET trả requiresReconciliation nếu được đánh dấu hoặc attempt mở đã hết hạn.
Return cũ trả JSON status lấy từ database, không dùng response browser để tuyên bố
thành công. Giai đoạn 2D.1 bổ sung endpoint /return/redirect cho browser; xem luồng
và cấu hình bên dưới. Endpoint JSON giữ nguyên để tương thích Frontend hiện có.
Không trả PII, secret, provider payload hoặc transaction nội bộ cho browser.

Lỗi chính: 400 request/return không hợp lệ; 403 không phải khách hoặc bên liên
quan không hoạt động; 404 không tìm thấy; 409 trạng thái/số tiền/đối soát/xung đột;
503 feature tắt, cấu hình thiếu, schema chưa sẵn sàng hoặc lỗi database tạm thời.

IPN dùng đúng JSON keys RspCode/Message, HTTP 200:
- 00: đã ghi nhận notification (bao gồm giao dịch được đưa vào đối soát).
- 02: đã xử lý; không tác động nghiệp vụ lần hai.
- 01: không tìm thấy reference/provider phù hợp.
- 04: số tiền sai.
- 97: chữ ký/merchant/tham số không hợp lệ.
- 99: chưa sẵn sàng/lỗi transaction; gateway cần retry.

## Transaction và bảo vệ dữ liệu

Create:
1. Schema guard chỉ đọc; bắt đầu Read Committed.
2. Khóa Vehicle -> Booking, đọc lại quan hệ xe; thay đổi cạnh tranh => retry.
3. Account/AccountRole của Customer và Owner được SHARE lock theo thứ tự ổn định.
4. Chỉ khách sở hữu Booking AwaitingPayment, chưa đến giờ nhận, xe Available,
   các bên Active được tạo. Giá lấy từ quoted_rental_amount; 0 < amount <=
   9,999,999,999 VND, không có phần lẻ, khớp giới hạn 12 chữ số sau nhân 100.
5. Chặn Booking đã có receipt Succeeded hoặc attempt chưa rõ của provider khác.
6. Khóa attempt, kiểm tra key và attempt đang mở; không tạo mới khi hết hạn/chưa
   đối soát. Reference UUID hex duy nhất, expires=min(now+TTL, pickup).
7. Ký URL thuần trong bộ nhớ và lưu attempt/url/key/expiry cùng transaction.

IPN:
1. Chặn query quá lớn, tham số lặp; sort ordinal và form URL encode (space '+').
2. Kiểm tra HMAC-SHA512 bằng so sánh constant-time, merchant, định dạng reference,
   amount, response/status và transaction number/pay time khi thành công.
3. Tra reference đúng provider VNPAY_SANDBOX. Khóa Vehicle -> Booking -> Payment;
   kiểm tra snapshot, bên tham gia, receipt khác và mã giao dịch dùng nơi khác.
4. Chống notification trùng bằng fingerprint SHA256 + unique(payment_id,fingerprint).
5. Ghi notification đã xác minh, update Payment, và chỉ khi policy cho phép thì
   UPDATE Booking AwaitingPayment -> Confirmed; commit tất cả hoặc rollback tất cả.
6. Unique indexes bảo vệ key, attempt mở, receipt được chấp nhận và mã giao dịch.
   Xung đột ở race giữa các Booking rollback và IPN 99 để gateway retry; lần sau
   có thể nhận diện transaction đã được dùng và đưa vào đối soát.

Không cập nhật bất kỳ cột giá, thời gian thuê, bên tham gia nào của Booking.
Statement timeout 10s và lock timeout 3s cho transaction Payment. Không log secret,
raw query hay callback payload trong implementation. Hạ tầng cần tắt/redact query
string/request body logs trên các route payment vì URL checkout có chữ ký.
ForwardedHeaders hiện tại của ứng dụng phải được giới hạn proxy tin cậy trước
khi triển khai công khai; ipAddress cho URL lấy từ connection, không từ body.

## Trạng thái bất thường, hết hạn và đối soát

- Chỉ cặp response/status 00/00 là receipt thành công để xem xét Confirmed.
- Response thất bại nằm trong whitelist và transaction status=02 mới là definitive
  failure; response 24 => Cancelled, các failure còn lại => Failed.
- Response 07 (có thể đã trừ tiền/nghi ngờ), trạng thái pending/reversal, mã lạ hoặc
  tổ hợp không rõ => Processing + needs_reconciliation; chặn thanh toán lại.
- Pending/Processing hết hạn không tự coi là không có tiền. GET tính cờ cần đối
  soát; POST đánh dấu EXPIRED_AWAITING_GATEWAY và chặn attempt mới.
- Thành công đến sau expiry (kể cả IPN chậm), sau failed/cancelled, sau khi Booking
  không còn payable, giá Booking đổi, bên tham gia/xe không hợp lệ, đã có receipt
  khác, transaction number dùng lại hoặc thời điểm bất thường => ghi Succeeded
  kèm needs_reconciliation=true, không Confirmed.
- Callback thất bại đến sau Succeeded không hạ trạng thái hoặc hủy Booking.
- Callback thành công lặp cùng transaction không xác nhận lần hai. Nếu cùng
  reference có transaction number khác, giữ receipt đầu và ghi đầy đủ evidence
  trong payment_notification, đánh dấu đối soát.

**Chưa triển khai QUERYDR, worker đối soát, API xử lý đối soát, refund hay tự giải
phóng Booking hết hạn.** Không dùng SQL chỉnh tay để bỏ cờ rồi thu lại tiền.
Attempt không rõ sẽ được giữ/chặn cho đến khi bổ sung quy trình đối soát an toàn.
Điều này ưu tiên tránh thu trùng, chưa phải luồng vận hành hoàn chỉnh.

## Cấu hình

ASP.NET Core environment / secret local:
- Payment__Enabled=false
- Payment__CheckoutMinutes=15 (1–30)
- VnPay__TmnCode: sandbox merchant code, 8 ký tự chữ/số
- VnPay__HashSecret: secret sandbox; không lưu trong appsettings được Git theo dõi
- VnPay__ReturnUrl: HTTPS absolute tới /api/payments/vnpay/return/redirect, không query/fragment
- PaymentReturn__FrontendResultUrl: URL cố định tới Frontend /payment/result (xem 2D.1)

Compose đọc PAYMENT_ENABLED, PAYMENT_CHECKOUT_MINUTES, VNPAY_TMN_CODE,
VNPAY_HASH_SECRET, VNPAY_RETURN_URL từ môi trường deployment. File .env không tự
được ASP.NET Core local đọc. IPN URL được đăng ký riêng với VNPay, HTTPS public
/api/payments/vnpay/ipn; không phải tham số vnp_ReturnUrl gửi trong URL checkout.
Không bật Payment trên database nhóm trong giai đoạn này. Sau khi đã vận hành,
không tắt toàn bộ flag khi còn giao dịch đang chờ mà không có kế hoạch nhận IPN.

## Kiểm thử và giới hạn xác minh

Unit test dùng synthetic key cố định chỉ cho test; signature vector tính độc lập
bằng Node crypto. TestHost không khởi chạy Program hoặc đăng ký database. Policy
và service được kiểm tra bằng mock; không dùng fake để khẳng định SQL đã đúng.

Khi chạy test đợt này, xóa cả BONBON_TEST_POSTGRES_ADMIN/CONSENT và
BONBON_SHARED_POSTGRES_CONNECTION/CONFIG_FILE trong tiến trình test.
Các PostgreSQL integration cũ và shared read-only đều SKIP. Không tạo database mới.

Kết quả cuối đợt 2C: `dotnet build` Release thành công (0 lỗi, 0 cảnh báo ở lần
build cuối); `dotnet test` có 137 passed, 0 failed, 12 nhóm PostgreSQL skipped.
Trong đó 90 case mới thuộc Payment; 47 case unit/API/safety có sẵn vẫn pass.
Hai read-only shared test từng pass ở 2B.1 không được chạy lại trong đợt này.

Chưa xác minh: thực thi migration proposal, cú pháp và mapping Npgsql runtime,
rollback/audit atomicity thật, unique index race/deadlock thật, schema guard trên
database đã nâng cấp, IPN HTTPS từ VNPay, mã merchant/secret sandbox thực tế,
signature end-to-end và trải nghiệm browser. Lỗi trigger protect_booking_quote
khác baseline của giai đoạn 2B.1 vẫn cần nhóm review, không tự sửa trong 2C.

Tài liệu chính thức đã đối chiếu:
- https://sandbox.vnpayment.vn/apis/docs/thanh-toan-pay/pay.html
- https://sandbox.vnpayment.vn/apis/docs/bang-ma-loi/

## Kế hoạch Frontend tại cuối 2C (đã triển khai ở 2D/2D.1)

Thêm payment-api/types, nút Thanh toán cho AwaitingPayment, giữ cùng idempotency
key khi retry mạng. Chỉ redirect tới checkoutUrl do Backend trả khi còn hiệu lực.
Trang kết quả đọc GET bằng JWT, hiển thị đang xác minh/đối soát; không gán thành
công dựa trên query string hoặc tự chuyển Booking sang Confirmed. Xử lý 409 đang
có attempt và 503 disabled/schema rõ ràng. Chỉ nối sandbox end-to-end sau khi
schema được duyệt và có môi trường integration ghi được phép.

## Giai đoạn 2D.1 — Return browser, cấu hình và kiểm thử

Luồng khuyến nghị: VNPay → GET API /api/payments/vnpay/return/redirect →
HandleReturnAsync xác minh HMAC/merchant/reference/amount và đọc payment → HTTP
302 tới URL Frontend cố định + ?paymentId=<Guid> → FE GET /api/payments/{paymentId}
bằng JWT Customer. Chỉ GET có phân quyền cung cấp kết quả hiển thị. Return không
gọi ProcessNotificationAsync, không cập nhật Payment/Booking. IPN đến muộn được
hiển thị bằng polling GET có giới hạn; không dựa vào vnp_ResponseCode=00.

PaymentReturn.FrontendResultUrl mặc định rỗng, cần cấu hình qua environment/secret
local, ví dụ https://frontend.example.test/payment/result. Chỉ nhận URL absolute
HTTPS, đúng path /payment/result, không userinfo/query/fragment. HTTP chỉ được
phép với loopback cho local development. Host/port lấy nguyên từ cấu hình tin cậy,
không lấy từ request, Host/Referer hoặc query returnUrl/redirectUrl/next.
Compose ánh xạ PAYMENT_FRONTEND_RESULT_URL sang PaymentReturn__FrontendResultUrl.
Không đổi cấu hình đang chạy hoặc bật flag trong giai đoạn này.

Destination thiếu/sai: HTTP 503, không redirect và không gọi service/database.
Callback sai chữ ký/reference/amount/tham số: redirect tới cùng URL cố định với
?returnError=invalid. Feature/schema/dịch vụ chưa sẵn sàng: ?returnError=unavailable.
Không chuyển tiếp ID chưa xác minh, status, chữ ký hay payload VNPay. Khi không thể
tra reference an toàn thì không đoán paymentId; khách mở lại Chuyến của tôi để xem
attempt đã lưu hoặc liên hệ hỗ trợ. FE không tạo giao dịch mới từ trang lỗi.
302 có Cache-Control no-store và Referrer-Policy no-referrer để callback query không
được gửi làm referrer sang FE. Cần tiếp tục redact query tại proxy/application logs.

Kiểm thử cuối 2D.1:
- dotnet build BonBon.sln --no-restore --configuration Release: thành công, 0 warning/error.
- dotnet test: 156 passed, 0 failed, 12 PostgreSQL tests skipped; biến môi trường
  kích hoạt integration được xóa trong tiến trình test. Không kết nối database.
- 19 case mới kiểm tra redirect cố định, chặn destination sai, callback trùng key,
  lỗi unavailable/invalid, JSON contract cũ, và pipeline Controller→Service→Gateway
  thật bằng signed vector với repository strict mock: không có bất kỳ lời gọi ghi.
- Frontend: 63 unit/UI tests và 36 Edge headless browser tests passed; browser
  dùng dữ liệu synthetic, intercept toàn bộ API và chặn dịch vụ ngoài localhost.

Chưa phải sandbox end-to-end: vẫn thiếu review/áp dụng proposal 004 trong môi
trường được phép, PostgreSQL integration có ghi, merchant sandbox thật và HTTPS
Return/IPN/CORS/SPA fallback đã kiểm tra trên hosting thật. Quy trình QUERYDR/đối
soát/refund và vấn đề trigger đã ghi nhận trước đó vẫn chưa hoàn tất.
