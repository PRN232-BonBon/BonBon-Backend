# BonBon — Payment code review & handoff

Ngày bàn giao: 23/10/2026. Nhánh: `feature/payment` ở cả hai repository.
Phạm vi: Booking Approval/Reject và thanh toán **toàn bộ tiền thuê bằng VNPay sandbox**.
Wallet, chia tiền chủ xe, hoàn tiền và cổng VNPay production không thuộc phần đã code.
Nhóm tiếp nhận phụ trách database, HTTPS, sandbox end-to-end và triển khai.

## 1. Trạng thái bàn giao

Source và các kiểm tra local được bàn giao; **không đồng nghĩa sẵn sàng production**.
Payment mặc định tắt trong `PaymentOptions`, `appsettings.json`, compose mặc định false.
Không tự tạo schema/migration hoặc kết nối gateway tại startup thông thường.
Gateway chỉ chấp nhận URL sandbox cố định, chưa hỗ trợ URL production.
Merchant sandbox đã được người dùng kích hoạt; credential local đã kiểm tra bind ở
giai đoạn trước, không được đưa vào tài liệu hoặc Git. HTTPS/IPN công khai và giao dịch
VNPay thật chưa xác minh. Không cần Codex tiếp tục ngrok/domain/tunnel.

Trong lượt review cuối không kết nối DB, không kiểm tra merchant local lại, không tạo
attempt, không thực thi migration hoặc giao dịch. Kết quả PostgreSQL của 2E.2 trong
`payment-postgres-tests-2e2.md` là bằng chứng lịch sử, không phải lần chạy lại hiện tại.

## 2. Kiến trúc và các điểm vào

`PaymentController → IPaymentService/PaymentService → IPaymentRepository/PaymentRepository → PostgreSQL`.
`VnPayGateway : IPaymentGateway` chỉ tạo/kiểm tra chữ ký local, không gọi mạng.
`PaymentNotificationPolicy` quyết định nghiệp vụ từ snapshot khóa bởi repository.
DI đăng ký trong `BonBon.API/Program.cs`; không đưa Npgsql hoặc secret vào Frontend.
Các model Payment hiện có được giữ; repository dùng record trong `PaymentContracts.cs`
và Npgsql SQL trực tiếp để sử dụng cột mới, không có EF migration tự động.

Frontend: `TripsPage → PaymentButton → payment-api.ts`, lưu thao tác trong
`payment-session.ts`; `PaymentResultPage` lấy trạng thái từ API, trình bày bằng
`payment-presentation.ts`. Các DTO TypeScript trong `payment-types.ts` khớp camelCase
DTO Backend. Route `/payment/result` đăng ký trong `App.tsx`.

## 3. API và contract

| Method / path | Quyền | Hành vi |
|---|---|---|
| POST `/api/bookings/{bookingId}/approve` | JWT VehicleOwner + kiểm tra chủ xe hiện tại trong DB | PendingApproval → AwaitingPayment; replay cùng quyết định trả200/isReplay |
| POST `/api/bookings/{bookingId}/reject` | Như approve | PendingApproval → Rejected; không được đổi ngược quyết định |
| GET `/api/bookings/mine` | JWT, giới hạn account từ claim | Danh sách booking của account; `totalAmount` lấy quoted_rental_amount |
| POST `/api/payments` | JWT Customer + account/role active, đúng khách đặt | Body chỉ `{ "bookingId": "UUID" }`; cần đúng1 header Idempotency-Key |
| GET `/api/payments/{paymentId}` | JWT Customer + booking thuộc account và role active trong DB | Trạng thái thực tế, không public lookup |
| GET `/api/payments/vnpay/ipn` | Anonymous ở transport; bắt buộc xác minh callback tại gateway/service | Nơi cập nhật Payment/Booking/audit |
| GET `/api/payments/vnpay/return` | Anonymous | Legacy JSON, chỉ verify và đọc giao dịch, không cập nhật |
| GET `/api/payments/vnpay/return/redirect` | Anonymous | Return khuyến nghị:302 về Frontend cố định sau kiểm tra |

Không nhận giá, owner/customer, payment status hoặc destination từ Frontend.
Idempotency-Key16–100 ký tự `[A-Za-z0-9_-]`, scope `(booking_id,idempotency_key)` cho
provider `VNPAY_SANDBOX`. Booking phải AwaitingPayment, xe Available, trước giờ nhận;
account và role Customer/VehicleOwner đều phải active để tạo thanh toán.

POST200 `PaymentCheckoutResponse`:

```text
paymentId:string(UUID), bookingId:string(UUID), amount:number, currency:string("VND"),
status:string, checkoutUrl:string|null, expiresAt:ISO8601, isReplay:boolean
```

GET200 `PaymentStatusResponse`: cùng `paymentId,bookingId,amount,currency,status,expiresAt`,
thêm `requiresReconciliation:boolean`; không có checkoutUrl/isReplay.
Return JSON200: `paymentId,status,requiresReconciliation,message`.
Status chính xác: `Pending | Processing | Succeeded | Failed | Cancelled`.
Reconciliation là **flag riêng**, không thêm enum status; Succeeded vẫn có thể cần đối soát.
Không hiển thị thành công xanh khi `requiresReconciliation=true`.

Payment lỗi trả ProblemDetails có `title`=mã lỗi, `detail`, `status`:

| HTTP | Trường hợp |
|---|---|
| 400 | PAYMENT_REQUEST_INVALID, PAYMENT_RETURN_INVALID hoặc model binding |
| 401 | Thiếu/sai/hết hạn JWT hoặc claim account không hợp lệ |
| 403 | Sai role ở middleware; PAYMENT_FORBIDDEN, PAYMENT_ACTOR_INACTIVE |
| 404 | BOOKING_NOT_FOUND, PAYMENT_NOT_FOUND; lookup không thuộc khách cũng404 |
| 409 | BOOKING_NOT_PAYABLE, PAYMENT_AMOUNT_INVALID, BOOKING_ALREADY_PAID, PAYMENT_IN_PROGRESS, PAYMENT_RECONCILIATION_REQUIRED |
| 503 | PAYMENT_DISABLED, PAYMENT_CONFIGURATION_INVALID, PAYMENT_SCHEMA_UNAVAILABLE, PAYMENT_TEMPORARILY_UNAVAILABLE |

Booking decision:400 ID rỗng,401 JWT,403 không phải chủ/owner inactive,404 không có booking,
409 state/lịch/xe/customer inactive/timeout xung đột. Response200 gồm
`bookingId,status,unitPrice,quotedRentalAmount,currency,isReplay`.

IPN dùng HTTP200 với **`RspCode` và `Message` viết hoa đúng contract VNPay**:
00 applied (bao gồm đã ghi nhận cần đối soát, không có nghĩa Booking luôn Confirmed),
02 duplicate,01 unknown reference,04 amount invalid/mismatch,97 chữ ký/merchant/tham số
không hợp lệ,99 unavailable/lỗi tạm thời. Không dùng HTTP200 làm bằng chứng nhận tiền.

## 4. Luồng và tính nhất quán

1. Booking được tính giá bởi Backend/DB, tạo PendingApproval. Owner thực sự sở hữu
   xe duyệt/từ chối. Approve kiểm tra account/role, xe, giờ nhận và khoảng lịch `[start,end)`;
   kiểm tra booking giữ lịch và availability_block. Không sửa snapshot/quoted amount.
   Reject không cần xe Available/customer active vì không giữ lịch mới; vẫn kiểm tra owner.
2. Customer bấm thanh toán từ `/bookings/mine`. Frontend giữ key theo account+booking
   trong localStorage trước POST, chống double-click; retry mạng giữ key, không tự rotate.
3. Repository khóa **Vehicle → Booking**, kiểm tra participants, Payment, khóa account/role
   theo thứ tự ổn định; lấy amount từ quoted_rental_amount. UUID mới làm reference32 ký tự.
   Thời hạn attempt=min(now+CheckoutMinutes, booking.start_at). Ký local trước commit.
4. Backend trả checkout URL đã lưu; Frontend chỉ chuyển tới đúng HTTPS host/path sandbox,
   giữ paymentId để tra cứu. Cùng key dùng lại attempt/URL; khác key khi còn attempt mở
   bị409. Attempt hết hạn/không rõ/cần đối soát chặn tạo lần khác.
5. VNPay IPN: form URL encode, sort ordinal, HMAC-SHA512, so sánh constant time; kiểm tra
   TmnCode, reference, amount nguyên VND×100, response/status, transactionNo, ngày GMT+7.
   Service chỉ đưa notification hợp lệ vào repository.
6. Trong cùng transaction ReadCommitted: khóa Vehicle→Booking→Payment, kiểm tra số tiền,
   callback fingerprint, participants, receipt khác, provider transaction; INSERT audit,
   UPDATE payment; chỉ decision Success mới đổi AwaitingPayment→Confirmed, rồi commit.
   Lỗi rollback toàn bộ; timeout lock3s/statement10s. Approval lock5s/statement15s.
7. Chữ ký/số tiền sai không ghi audit thành receipt hợp lệ. Callback fingerprint trùng
   không ghi lại; callback hợp lệ khác fingerprint có audit riêng. Unique transaction
   xử lý tranh chấp giữa booking; Npgsql conflict trả99 để provider retry, không tự retry charge.
8. Chỉ response00/status00 là success. Thành công đến muộn, sau thất bại, sai trạng thái
   booking, khác amount snapshot, transaction bị tái dùng hoặc participants inactive:
   ghi receipt Succeeded + needs_reconciliation, **không tự Confirmed booking**.
   Failure chắc chắn chỉ nhóm response09/10/11/12/13/24/51/65/75/79 kết hợp status02;
   response24 ghi Cancelled, các mã còn lại Failed. Kết quả khác Processing/cần đối soát.
9. Failure xác định đến sau success không đảo receipt. Callback đảo/không rõ sau success
   giữ Succeeded nhưng đánh dấu cần đối soát (`GATEWAY_RESULT_AFTER_SUCCESS`). Không tự
   hủy Booking đã Confirmed hoặc hoàn tiền; cần xử lý vận hành trước khi giao xe.
10. Return chỉ verify+read.302 về `PaymentReturn.FrontendResultUrl` cố định, chỉ truyền
    paymentId resolve từ DB hoặc returnError=invalid/unavailable; bỏ qua redirectUrl,
    status và paymentId tự gửi trong query. Frontend phải JWT GET status sau đó.

Frontend request timeout15s, polling5s tối đa12 lần; lỗi mạng/401/403/404/409/503 dừng
polling và thông báo tiếng Việt. Refresh/manual check không tạo Payment. Truy cập
trực tiếp/reload cần SPA fallback về index.html. Mất phiên giữ URL để đăng nhập lại.

## 5. Database và migration đề xuất

**Không thực thi trong review cuối.** Hai file vẫn nằm `database/proposals`:

- `004_vnpay_sandbox.sql`: thêm9 cột payment: idempotency_key,checkout_url,expires_at,
  provider_transaction_no,response_code,transaction_status,updated_at,
  needs_reconciliation,reconciliation_reason. Giữ payment_id/booking_id/provider/
  provider_reference/amount/currency/status/created_at/paid_at hiện có.
- CHECK attempt giới hạn provider sandbox;4 unique partial indexes: idempotency theo
  booking+key, active Pending/Processing theo booking, success được chấp nhận theo
  booking và NOT needs_reconciliation, transactionNo không null. Audit table
  payment_notification FK RESTRICT+unique(payment_id,fingerprint), index(payment_id,received_at),
  các CHECK định dạng/receipt/reconciliation. Marker payment_gateway_schema version1.
- `005_booking_quote_guard.sql`: thay function protect_booking_quote, giữ trigger/owner/ACL;
  cấm đổi customer/vehicle, quay lại PendingApproval; cấm đổi giá/time/duration khi rời
  PendingApproval hoặc sau quyết định. quoted_rental_amount phải là generated expression
  `billable_units * unit_price_snapshot`, không được client ghi trực tiếp.

004/005 không sửa wallet/wallet_transaction, không xóa payment cũ;004 mặc định flagfalse
cho dữ liệu cũ, không backfill thành sandbox receipt.004 không tự cấp quyền app role,
không tự sửa trigger Booking. Không chạy001→002→003 máy móc:001 hiện là consolidated
flexible schema,002 là upgrade baseline cũ. Main từng có quote guard002 yếu hơn001;
005 khắc phục decision-time mutation và participants, cần DBA kiểm tra baseline thật.

004 là one-shot, preflight từ chối partial/rerun; transaction+timeout, ACCESS EXCLUSIVE
trên payment và tạo index trong transaction có thể chặn ứng dụng. Backup đã thử phục
hồi, maintenance window, review toàn bộ diff/schema/grants/index predicates/constraints/
exclusion/functions bởi nhóm trước khi áp dụng target được phép. Marker runtime chỉ
kiểm tra một phần schema, không thay thế kiểm tra metadata.005 không tự tạo trigger nếu thiếu.
004 và005 là hai transaction riêng:005 lỗi không tự undo004 đã commit; giữ Payment tắt.
Lỗi trước COMMIT rollback/disconnect; sau commit có receipt phải roll forward/restore
có kế hoạch, không drop cột/audit hoặc mù quáng dùng down migration.

## 6. Cấu hình môi trường và secret

| Biến Backend | Yêu cầu |
|---|---|
| `ConnectionStrings__DefaultConnection` | Secret DB target riêng, role tối thiểu, không superuser |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Key secret mạnh, issuer/audience riêng từng môi trường |
| `Payment__Enabled` | Mặc địnhfalse; chỉ bật sau DB/merchant/HTTPS/đối soát đã sẵn sàng |
| `Payment__CheckoutMinutes` | 1–30, mặc định15; chỉ TTL attempt |
| `VnPay__TmnCode`, `VnPay__HashSecret` | Merchant sandbox thật, chỉ Backend |
| `VnPay__PaymentUrl` | Chỉ https://sandbox.vnpayment.vn/paymentv2/vpcpay.html |
| `VnPay__ReturnUrl` | HTTPS Backend + /api/payments/vnpay/return/redirect |
| `PaymentReturn__FrontendResultUrl` | Frontend cố định + /payment/result; không query/fragment/userinfo |
| `Cors__AllowedOrigins__N` | Chỉ origins đã duyệt; configure trusted proxy cụ thể |
| `Google__ClientId` | Client ID công khai, không phải HashSecret; cấu hình Auth của nhóm |

Frontend `VITE_API_URL` gồm prefix `/api` (khuyến nghị cùng origin),
`VITE_VNPAY_SANDBOX_URL` chỉ được bằng gateway cố định, `VITE_GOOGLE_CLIENT_ID` nếu
dùng Google Auth. Mọi `VITE_*` là public build-time, tuyệt đối không chứa credential/JWT key.
Thay VITE_* cần build lại; staging config riêng ép `/api` và tắt Google ID từ .env production.

Profile local `PaymentStaging` dùng `BONBON_PAYMENT_STAGING_CONFIG` absolute path tới
`BonBon.API/.payment-staging.local.json`; JSON dùng key lồng nhau, env dùng `__`.
Các biến thêm: PaymentStaging__PublicOrigin, PaymentStaging__IpnUrl,
PaymentStaging__ExpectedDatabaseHost (pin host riêng bắt buộc, chỉ lưu local/secret config),
PaymentStaging__CallbacksVerified, PaymentStaging__MerchantVerified,
PaymentStaging__ReconciliationPlanVerified và AllowedHosts. Origin cố định, ba URL cùng
origin, từ chối domain production; các flag không phải bằng chứng tự động và không được
settrue chỉ để vượt startup guard. IpnUrl là giá trị kiểm tra cấu hình, không tự đăng ký với VNPay.

Profile này **cố ý giới hạn môi trường test đã được phê duyệt**: host phải khớp pin
ExpectedDatabaseHost khai báo độc lập trong cấu hình local, không suy ra từ connection string;
database bonbon_payment_test, role bonbon_payment_test_runner, PostgreSQL16.15,
role owner/connection limit4, pool2, marker testDB và004/005. Đây không phải cấu hình
production tổng quát; nhóm dùng máy khác phải review lại guard/profile, không đổi sang
credential bonbon_app hoặc bỏ guard để chạy. Các localhost URL là dev/test, không public callback.

Fixture shared/provisioning cần `BONBON_PAYMENT_EXPECTED_DB_HOST` hoặc
`PaymentStaging.ExpectedDatabaseHost` trong file secret source chỉ định. Thiếu pin,
host khác hoặc pin wildcard/multi-host/URL sẽ dừng trước khi connect. Initializer mới
cần `-ExpectedDatabaseHost` do người quản trị xác nhận độc lập. Unit tests chỉ dùng
`db.example.test`, không có địa chỉ máy thật. Các kiểm tra database/session user/owner,
marker, server identity, role/quyền, version và connection limit được giữ nguyên.

Development credential trước đây nằm trong file tracked đã được xóa khỏi source và
giữ tại `BonBon.API/appsettings.Local.json` ignored; chỉ Development nạp file đó,
env/command-line vẫn override. Local file có credential môi trường cũ: không dùng nó
để chạy test Payment và không truyền cho thành viên qua Git. Không tự start Development
API nếu chưa xác minh target. Secret local không được trao đổi qua chat/log/ảnh.
API project và Docker context loại config local khỏi build/publish. Tuy vậy build artifact
cũ/caches có thể đã chứa secret; nhóm quản lý việc dọn artifact và **rotate credential
connection/JWT đã từng ở Git**, không tự rewrite history/đổi DB password trong review này.

Production reverse proxy hiện có được giữ nguyên ngoài phần bổ sung staging:
KnownNetworks/KnownProxies bị clear ở cấu hình legacy nên nhóm phải siết trusted proxies,
host filtering/TLS/rate limits trước deploy. Không public Swagger/debug/test/DB.
IPN GET phải anonymous ở edge (không JWT/IP tester/login/challenge) nhưng service vẫn
xác minh HMAC/merchant/reference/amount; route human có thể giới hạn tester + JWT.
Caddy file chỉ là đề xuất edge trực tiếp, không phải cấu hình tunnel đã validate.

## 7. Kiểm thử được phép và sandbox về sau

Kiểm tra local không dịch vụ bên ngoài:

```powershell
# Xóa opt-in trong process test; không ghi các biến này vào cấu hình hệ thống.
Get-ChildItem Env:BONBON_TEST_POSTGRES*,Env:BONBON_SHARED_POSTGRES*,Env:BONBON_PAYMENT_TEST*,Env:BONBON_PAYMENT_STAGING*,Env:BONBON_PAYMENT_MERCHANT_LOCAL_CHECK -ErrorAction SilentlyContinue | Remove-Item
dotnet build BonBon.sln -c Release --no-restore
dotnet test BonBon.Tests/BonBon.Tests.csproj -c Release --no-build --no-restore
# Từ BonBon-Frontend:
npm run build
npm run typecheck:payment-staging
npm test
npm run test:browser
```

Browser regression dùng Edge/Playwright, intercept toàn bộ API và chặn origin ngoài,
desktop/mobile. Không dùng `test:payment-staging` hoặc fixture prepare vì chúng có thể
login/register/provision dữ liệu thật vào testDB. Merchant local check cũng opt-in,
không cần bật để chạy unit suite. Không coi các test mock là bằng chứng E2E.

Nhóm tiếp nhận thực hiện riêng khi đã được phép:

1. Backup phục hồi được, DB test/role/marker cô lập; schema clone không có dữ liệu cá
   nhân; review/apply004 và005 chỉ trên target đã duyệt. Kiểm metadata, grants và
   thử rollback/atomic/unique/lock concurrency với tải nhỏ. Không dùng DROP fixture
   trên server nhóm. Các opt-in fixture và safety checks xem docs2E.2/source tests.
2. Chuẩn bị Customer/VehicleOwner synthetic/Available vehicle, tạo và duyệt booking
   bằng luồng API; kiểm snapshot/giờ/owner. Không dùng account production.
3. HTTPS riêng do nhóm quản lý, callback thật, secrets sandbox, IPN đăng ký với VNPay;
   xác nhận hostname phù hợp merchant. Return frontend cố định, CORS/JWT/SPA fallback.
4. Thử success/failure/cancel, replay request/key, khác customer, inactive role,
   double-click, callback lặp/đến muộn/trái thứ tự/sai amount/signature/reference,
   IPN mất hoặc chậm, reload/mất phiên/network và cần đối soát. Đối chiếu receipt/audit/
   Booking atomic trong DB test. Không replay callback vào booking của người thật.
5. Phê duyệt kế hoạch reconciliation, TTL và việc xử lý tiền đã nhận nhưng booking
   không Confirmed; sau đó mới bật flag staging và thử sandbox. Không dùng tiền/thẻ thật.

## 8. Giới hạn cần giải quyết trước production

- Chưa có Booking payment deadline/worker giải phóng AwaitingPayment; booking có thể
  giữ lịch vô hạn. Không tự hủy booking theo expires_at khi tiền chưa rõ.
- Chưa có QUERYDR, job đối soát, màn hình/API xử lý reconciliation, refund/reversal
  workflow hoặc quyết định lại booking sau xử lý. Flag/audit đã có, xử lý tự động chưa có.
- Receipt Succeeded+needs_reconciliation không đồng nghĩa booking Confirmed; ngược lại
  reversal sau thành công có thể giữ Booking Confirmed, phải xử lý trước khi giao xe.
- Chưa có API lịch sử Payment theo Booking/recovery khi mất localStorage, retry có
  chủ đích sau Failed/Cancelled hoặc đổi thiết bị. UI giữ key cũ nên không tự tạo attempt
  mới sau terminal failure; nhóm phải thiết kế retry/recovery rõ ràng, không reset storage mù quáng.
- Chưa có decision audit/approved_by/rejected_reason cho Booking hoặc deadline cột riêng.
- Chưa bảo đảm chống writer ngoài repository/cổng khác thay đổi payment/booking theo
  thứ tự khóa khác. Partial indexes chủ yếu scope sandbox; phải review mọi integration.
- No load/fault injection trên server chung; network/actual PostgreSQL behavior sau
  sửa review cuối phải chạy lại trên DB cô lập. Unique-race retry IPN chưa được thử
  với nhà cung cấp; không tự retry thao tác charge.
- Chưa có cổng production, monitoring/alert/reconciliation runbook hoàn chỉnh, kiểm
  thử signature/provider thực tế, TLS/IPN public, migration/grants của mainDB.
- Unit/browser pass không chứng minh isolation PostgreSQL, migration runtime, hoặc VNPay E2E.

## 9. Thay đổi của review cuối và danh mục working tree

Kết quả lần chạy cuối:

| Kiểm tra | Kết quả |
|---|---|
| `dotnet build BonBon.sln -c Release --no-restore` | Thành công,0 warnings/0 errors |
| Unit/API .NET, mọi opt-in external tắt |184 passed,0 failed,42 skipped,total226 |
| `npm run build` |Thành công |
| `npm run typecheck:payment-staging` |Thành công |
| `npm test` |69 passed,0 failed |
| `npm run test:browser` |36 passed,desktop/mobile,API bị intercept |
| `git diff --check` cả hai repo |Không có lỗi whitespace |
| Scan known operational credentials trên file tracked/untracked được Git đưa vào |Không có hit,không in giá trị |

42 skipped gồm các fixture PostgreSQL/shared read-only/prepare/staging và merchant
local opt-in. Không bật chúng trong review này. Chưa build Docker image (Dockerfile
chỉ kiểm tra tĩnh); chưa publish/deploy, không gọi external gateway hoặc database.
Đã dừng tiến trình Backend staging của phiên trước để build, không tự khởi động lại.
Đã dọn2 bản sao Development config cũ trong build output, giữ file local nguồn.
Credential từng ở Git vẫn cần nhóm rotate; clean working tree không xóa lịch sử Git.

Working tree tổng cộng: Backend16 tracked modified+50 untracked files; Frontend8
tracked modified+21 untracked files. Đây là toàn bộ work chưa commit từ nhiều giai đoạn,
không phải15 file sửa/tạo riêng trong review cuối. Không bao gồm ignored secret/artifacts.

### Security review bổ sung trước public push

Kết quả sau sửa security: Release build0 warnings/0 errors;191 unit/API passed,
0 failed,42 integration/merchant opt-in skipped. Frontend build/typecheck đạt,
69 unit tests passed. Không kết nối DB/VNPay/staging E2E. Phát hiện và dọn7 file
build cũ chứa metadata riêng; không xóa source/config local. Host pin local đã được
giữ đúng target cũ, chỉ so khớp offline, không truy vấn DB. Merchant/Payment flag không đổi.

- Đã chuyển host pin từ source sang cấu hình local được ignore, bắt buộc so khớp
  độc lập; không làm yếu database identity/marker/role/grants. Kiểm thử regression
  missing/mismatch/broad pin hoàn toàn offline.
- Đã bỏ host/IP thật, fingerprint catalog, ID seed và đường dẫn máy cá nhân khỏi
  tài liệu public. Bằng chứng chi tiết giữ ở archive local được ignore, không đưa Git.
- URL sandbox VNPay, domain website công khai, localhost, tên schema/database/role
  theo contract safety và dữ liệu synthetic rõ ràng không phải credential bí mật.
- Quản trị viên cần rotate/revoke mật khẩu của role DB từng nằm trong cấu hình
  Development tracked và JWT signing key cũ; rà soát session/refresh-token invalidation,
  truy cập DB và artifact/cache/image cũ có thể chứa config. Không nêu giá trị ở đây.
- Không có bằng chứng merchant secret, test-runner password hoặc JWT staging đã ở Git;
  giữ private local, không tự rotate chúng. Nếu quản trị viên phát hiện từng chia sẻ/
  publish các file local/artifact đó thì phải rotate tương ứng, kể cả secret merchant.
- Lịch sử Git public cũ vẫn chứa credential; làm sạch working tree không thu hồi
  credential. Không rewrite history hoặc đổi secret trong review này. Việc thu hồi
  credential và xử lý lịch sử/caches thuộc quản trị viên, không tự thực hiện.

Sửa policy đảo/uncertain sau success sang reconciliation; reject gateway date ngoài
DateTimeOffset range thay vì throw500; Frontend từ chối record localStorage sai kiểu/
paymentId rỗng/null, giữ record và không tạo key mới. Bổ sung regression tương ứng.
Tách credential Development khỏi source, loại local secrets khỏi build/Docker context,
restore API project thay vì solution trong Dockerfile (solution có thêm test project).
Không thay đổi DTO/endpoint, Booking pricing, migration004/005 hoặc thêm Wallet/refund.

Xem `PAYMENT_WORKTREE_MANIFEST.md` ở từng repository để biết **toàn bộ** file tracked
modified/untracked chưa commit, bao gồm các giai đoạn trước. Các file local ignored
chứa secret không được liệt kê hoặc bàn giao qua Git. Không có commit/push trong review.
