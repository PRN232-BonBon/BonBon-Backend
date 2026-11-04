# Giai đoạn 2E.1 — Review schema / proposal 004 (2026-10-23)

## Kết luận

004 là thiết kế mở rộng phù hợp cho Payment VNPay sandbox hiện tại, **chưa phải
migration đã được xác minh để áp dụng lên database nhóm**. Đã chỉnh file đề xuất,
không thực thi. Không tạo database, chạy DDL/DML nghiệp vụ hoặc bật Payment.
Không sửa API/Service/Repository/Booking, migration 001/002/003 hoặc Frontend.

Hai repository vẫn feature/payment, giữ nguyên các thay đổi chưa commit của các
giai đoạn trước. Không commit/push. Chỉ sửa/tạo ba file trong đợt review:
- database/proposals/004_vnpay_sandbox.sql — bổ sung preflight và CHECK.
- BonBon.Tests/Integration/SharedPostgresReadOnlyTests.cs — thêm probe catalog Payment.
- docs/payment-schema-review-2e1.md — báo cáo và kế hoạch kiểm thử.

## Bằng chứng database thực tế

Đã chạy riêng hai test catalog, dùng secret cấu hình hiện có trong bộ nhớ, không
in connection string/password. Chỉ SELECT pg_catalog/information_schema và đọc
identity của phiên; không SELECT bản ghi người dùng, không gọi create_booking(),
không FOR UPDATE. Kết thúc bằng rollback. Không dùng fixture tạo/xóa database.
Mỗi connection cưỡng chế default_transaction_read_only=on, pooling=false,
connect/statement timeout 5s, lock timeout 1s, idle-in-transaction timeout 10s.

Kết quả: bonbon, bonbon_app, PostgreSQL 16.15, transaction_read_only=on.
payment_gateway_schema và payment_notification đều chưa tồn tại; payment chưa có
9 cột mới. Constraint được kiểm tra đều validated. Payment không có custom trigger.
Owner của booking/payment/wallet/wallet_transaction là bonbon_app, hiện có quyền
SELECT/INSERT/UPDATE. Các quyền này chỉ được đọc metadata, không được sử dụng để ghi.

| Bảng | Schema đã xác minh | Tác động 004 |
| --- | --- | --- |
| booking | PK uuid; customer/vehicle uuid; billable_units integer; start/end timestamptz; unit_price_snapshot numeric(18,2); quoted_rental_amount GENERATED STORED = billable_units * unit_price_snapshot; currency VND; 10 trạng thái | Không ALTER, không đổi giá/trạng thái/hàm |
| payment | PK uuid, FK booking_id; provider_reference varchar(200) UNIQUE; provider varchar(50); amount numeric(18,2), VND nguyên; status Pending/Processing/Succeeded/Failed/Cancelled; paid_at/created_at timestamptz; Succeeded đòi paid_at | Thêm 9 cột, 1 CHECK, 4 partial unique indexes |
| wallet | PK uuid, account_id/role_id FK account_role; unique(account_id,role_id); role 2/3; currency VND; Active/Frozen/Closed | Không đọc/ghi bởi PaymentRepository; không thay đổi |
| wallet_transaction | PK uuid; FK wallet, booking nullable; idempotency_key varchar(200) UNIQUE toàn bảng; Credit/Debit, VND nguyên; Pending/Posted/Cancelled | Không thay đổi, không tạo bút toán Wallet |

wallet_transaction.idempotency_key và payment.idempotency_key là hai miền khác
nhau: không tái dùng unique của ledger để bảo vệ checkout Payment. Không có FK
payment_id trong wallet_transaction hiện tại; thanh toán thuê xe chưa ghi Wallet
hoặc chia tiền cho chủ xe. Đó là phạm vi chưa triển khai, không phải số dư đã nhận.

## Schema Payment Backend yêu cầu

| Cột mới payment | Kiểu / NULL | Nơi dùng |
| --- | --- | --- |
| idempotency_key | varchar(100), nullable với legacy | Create/replay theo booking + key |
| checkout_url | text, nullable với legacy | URL đã ký được lưu cùng attempt; không log |
| expires_at | timestamptz, nullable với legacy | ReadPayment/Get/Create/policy deadline |
| provider_transaction_no | varchar(15), nullable | Gắn receipt đầu tiên; chống tái sử dụng mã |
| response_code | varchar(2), nullable | Kết quả callback đã xác minh |
| transaction_status | varchar(2), nullable | Trạng thái provider, khác payment.status |
| updated_at | timestamptz, nullable với legacy | Create, callback, đánh dấu expired |
| needs_reconciliation | boolean NOT NULL DEFAULT false | GET, chặn attempt mới, policy/unique success |
| reconciliation_reason | varchar(100), nullable | Lý do đối soát có cấu trúc |

CHECK chỉ áp dụng provider=VNPAY_SANDBOX: non-null ref/key/url/expiry/updated_at;
ref alnum 1–100; key alnum/_/- 16–100; số tiền 1–9,999,999,999 VND; timestamp hữu
hạn; 0 < expires-created <= 30 phút; updated>=created; URL đúng prefix sandbox;
mã kết quả 2 chữ số; transaction number 1–15 chữ số, khác toàn số 0. Cờ đối soát
và reason phải nhất quán. Succeeded được chấp nhận phải có transaction number;
receipt cần đối soát có thể không ghi transaction number trên payment vì mã đó
đã thuộc payment khác, nhưng notification vẫn giữ bằng chứng đầy đủ.
Các ràng buộc VND nguyên/status/paid_at từ baseline vẫn được giữ nguyên.

| Index mới | Khóa / predicate | Bảo vệ |
| --- | --- | --- |
| uq_vnpay_sandbox_idempotency | (booking_id,idempotency_key), sandbox | Key không tạo attempt thứ hai |
| uq_vnpay_sandbox_active | (booking_id), sandbox Pending/Processing | Tối đa một attempt đang mở |
| uq_vnpay_sandbox_accepted_success | (booking_id), sandbox Succeeded AND NOT needs_reconciliation | Tối đa một receipt được chấp nhận |
| uq_vnpay_sandbox_transaction | (provider_transaction_no), sandbox non-null | Không dùng một mã cho hai payment |

Unique provider_reference toàn bảng hiện hữu tiếp tục bảo vệ tra reference; không
tạo index trùng. ix_payment_booking_created hiện hữu hỗ trợ truy vấn theo Booking.

payment_notification gồm UUID PK/default gen_random_uuid(); FK payment_id RESTRICT;
fingerprint hex64; provider_transaction_no; amount_minor bigint (100–999999999900,
chia hết 100); response_code/transaction_status; gateway_paid_at; received_at;
disposition varchar(30); reconciliation_reason varchar(100). Unique(payment_id,
fingerprint) + index(payment_id,received_at). CHECK xác minh hình dạng và tính nhất
quán receipt/đối soát. Các disposition thực sự được INSERT là Success, Failure,
ReconcileSuccess, ReconcilePending, Duplicate; InvalidAmount/InvalidReference trả
về trước INSERT. Audit không lưu raw query, secret, PII hoặc chữ ký.

payment_gateway_schema chỉ có version integer PRIMARY KEY CHECK(version=1), một
bản ghi 1 được commit cùng DDL. INSERT này chỉ nằm trong proposal chưa thực thi.
004 không tạo stored function/trigger nghiệp vụ mới; hai DO block là preflight
ẩn danh. Runtime dùng hàm có sẵn has_active_role(uuid,smallint), trigger booking
quote/schedule/actor, protect_vehicle_owner và extension btree_gist từ baseline.
create_booking() tiếp tục phục vụ Booking API, không được PaymentRepository gọi.

## Đối chiếu toàn bộ SQL / Npgsql

Không phát hiện tên cột hoặc kiểu dữ liệu sai giữa PaymentRepository và schema
sau 004. PaymentColumns có đúng 12 trường theo thứ tự ReadPayment: payment_id,
booking_id, provider_reference, amount, currency, status, created_at, expires_at,
checkout_url, needs_reconciliation, provider_transaction_no, paid_at. Guid/decimal/
string/bool/DateTimeOffset (UTC) phù hợp uuid/numeric/varchar/boolean/timestamptz.
Các cột nullable được reader kiểm tra, trừ reference/expiry đã bắt buộc qua CHECK
cho provider sandbox mà mọi SELECT của repository đều lọc.

- INSERT payment chỉ nhận giá đọc từ Booking, không ghi generated amount.
- GetForCustomer JOIN booking và has_active_role(@customer,2::smallint) khớp schema.
- GetByReference và locate notification dùng provider + reference UNIQUE hiện hữu.
- Replay/open dùng FOR UPDATE và đúng tên 9 cột mới.
- LockBooking đọc vehicle trước, khóa Vehicle → Booking, kiểm tra lại vehicle_id;
  LockParticipants đọc account + account_role với FOR SHARE; các role là smallint.
- INSERT audit đủ các cột mới, FK hợp lệ; enum/reason thực tế nằm trong độ dài cột.
- UPDATE payment và UPDATE booking status='Confirmed' nằm trong cùng transaction,
  cùng audit; Booking phải đang AwaitingPayment và affected-row count bằng 1.
- Tham số NULL được server suy kiểu từ cột/coalesce/so sánh hiện tại; chưa chạy
  binding Npgsql thật nên không khẳng định đã xác minh runtime SQL.

## Các vấn đề, mức độ và giới hạn

1. **Chặn rollout — quote guard bị yếu đi.** Metadata xác nhận body
   protect_booking_quote khớp 002, không khớp 001 hiện tại. Bản 002 không chặn đổi
   customer; cho đổi vehicle/giá/thời gian khi OLD PendingApproval/Rejected/Cancelled;
   không chặn status quay về PendingApproval. Sau khi lùi trạng thái có thể đổi
   quote. 001 giữ customer/vehicle bất biến luôn, cấm quay về PendingApproval và
   giữ giá/thời gian bất biến khi OLD không còn PendingApproval. Generated amount
   không ngăn thay các đầu vào. Payment kiểm tra BookingAmount khi IPN nhưng
   không thay thế được việc bảo vệ quyền sở hữu/lịch sử ở database.
2. **Chặn rollout — lịch sử migrations không tuyến tính.** 001 hiện tại là baseline
   đã hợp nhất flexible booking, không còn quantity/ck_booking_quantity. Chạy 002
   ngay sau 001 này sẽ lỗi RENAME quantity và DROP constraint. 003 không sửa quote
   guard. Shared đã có billable_units/create_booking 5 tham số: không chạy lại
   001/002/003 chỉ dựa trên số file. Cần manifest/checksum + schema preflight theo
   từng môi trường; 004 không tự chạy/chữa các migration trước.
3. **Đã sửa proposal — dữ liệu sandbox chưa được CHECK đủ.** Bản đầu chỉ kiểm
   tra cột quan trọng không rỗng; đã bổ sung giới hạn tiền, TTL, mã kết quả,
   fingerprint, receipt/audit/reconciliation và preflight column types/unique ref.
   Không backfill hay UPDATE dữ liệu Payment cũ của provider khác.
4. **Đã sửa proposal — race preflight provider.** ACCESS EXCLUSIVE lock payment
   được lấy trước kiểm tra có VNPAY_SANDBOX cũ, rồi giữ đến commit; không có cửa
   sổ writer chen vào giữa kiểm tra dữ liệu và ALTER. Timeout 2s làm abort nếu bận.
5. **Chưa xác minh — concurrency/atomicity.** Transaction thiết kế đúng nhưng
   unit mock không chứng minh FK/index/trigger, rollback, deadlock hoặc race thật.
   Mã giao dịch bị dùng đồng thời ở hai Booking sẽ có một transaction unique
   violation → rollback → IPN 99; retry sau đó mới nhận diện và đưa vào đối soát.
6. **Giới hạn multi-provider.** Các unique mới chỉ áp dụng sandbox. Repository
   đọc mọi Succeeded và attempt đang mở của provider khác dưới khóa Booking để
   tránh ghi trùng trong chính luồng này. Writer khác không tuân thủ khóa/policy
   vẫn có thể tạo nhiều receipt khác provider; chưa có global accepted-receipt
   constraint. Không mở nhiều payment writers trước khi chốt quy tắc toàn hệ thống
   và kiểm tra legacy duplicates trên môi trường được phép. Không tự mở rộng
   index toàn provider trong 004 vì có thể phá dữ liệu/nghiệp vụ cũ.
7. **Giới hạn schema guard runtime.** Guard kiểm tra tên/có mặt 9 cột, 4 index
   unique/valid/ready và version=1; không chứng minh column types/nullability,
   index keys/predicates/table, CHECK/audit FK/unique hoặc body trigger. Tên đúng
   nhưng định nghĩa sai có thể qua guard. Cần nâng guard/catalog integration ở
   giai đoạn triển khai tiếp theo, không sửa runtime trong đợt review này.
8. **Chưa có hạn giữ chỗ Booking.** expires_at thuộc từng Payment, không thuộc
   lần chủ xe duyệt; chưa có payment_due_at/approval timestamp trên Booking. GET
   hết hạn chỉ tính requiresReconciliation; Create mới đánh dấu expired. Không
   worker QUERYDR, timeout Booking hoặc tự giải phóng lịch; không dùng now() trong
   predicate index để giả lập deadline. Khoản đến muộn được ghi Succeeded +
   needs_reconciliation, không Confirmed. Cần chính sách TTL Booking/đối soát riêng.
9. **Audit chưa phải ledger bất biến.** Fingerprint dedupe ngăn xử lý lại cùng
   payload chuẩn hóa. Exact duplicate không thêm audit lần hai; khác payload
   đã xác minh được lưu disposition Duplicate nếu policy yêu cầu. Sai signature/
   amount/reference không được lưu vào payment_notification. Chưa có retention,
   append-only permissions/trigger hoặc lịch sử mọi lần đổi trạng thái (ví dụ
   FlagExpired không thêm notification). bonbon_app hiện là owner nên không được
   xem các bảng này là chống chỉnh sửa bởi admin/app owner. Các snapshot/key/ref/
   booking_id của Payment cũng chưa có trigger bất biến; runtime hiện không đổi
   chúng, nhưng writer ngoài luồng có thể sửa. Cần review quyền ghi và invariant
   này khi chốt mô hình audit, không dựa chỉ vào CHECK định dạng.
10. **Quyền và vận hành.** Nếu 004 được chạy bởi migration role khác owner app,
    hai bảng mới có thể không được app đọc/ghi. Cần review grants/default
    privileges, tách migration role khỏi runtime owner và cấp least privilege,
    không hardcode GRANT cho một role trong proposal. Checkout URL đã ký cần được
    bảo vệ khỏi log/export công khai. Cần đồng bộ thời gian server vì policy/TTL.
11. **TTL không phải bảo đảm không thu trùng ở gateway.** Không thể ngăn người dùng
    hoặc VNPay tạo nhiều giao dịch chỉ bằng index nội bộ; bằng chứng tiền nhận
    thêm được lưu để đối soát, không tự xác nhận/thu lại/hoàn tiền.

## Khắc phục quote guard an toàn — đề xuất riêng, chưa tạo/chạy migration

Không nhúng CREATE OR REPLACE protect_booking_quote vào 004 Payment. Chuẩn bị
migration riêng (ví dụ 005_booking_quote_guard) sau nhóm duyệt:
1. Lưu pg_get_functiondef, owner, ACL/search_path, trigger definition + hash hiện
   tại. Xác nhận guard 002 đã biết; gặp body khác thì dừng để review.
2. Dùng body guard mạnh của 001 hiện tại: customer/vehicle bất biến mọi trạng thái;
   không quay lại PendingApproval; quote/time bất biến sau khi có quyết định.
   Bổ sung quyết định rõ với nhóm: chỉ cho sửa quote/time khi cả OLD và NEW còn
   PendingApproval, để không sửa quote cùng câu UPDATE duyệt/từ chối. Bản 001
   hiện tại kiểm tra OLD.status nên chưa tự chặn trường hợp sửa giá đồng thời
   với PendingApproval → AwaitingPayment; service hiện chỉ UPDATE status và vẫn
   giữ giá, nhưng invariant này cần được test ở cấp database nếu nhóm chấp thuận.
   Giữ tên/signature, trigger hiện hữu, không disable trigger, không sửa dữ liệu cũ.
3. Test Pending sửa lịch/giá nếu nghiệp vụ cho phép; approve/reject giữ giá;
   Rejected/Cancelled/Confirmed không đổi quote hoặc quay về Pending; đổi customer/
   vehicle bị chặn; Payment chỉ đổi status vẫn hoạt động. Không tuyên bố guard
   này là máy trạng thái đầy đủ: các chuyển trạng thái khác vẫn cần service policy.
4. Triển khai transaction + lock timeout trên môi trường được cho phép, backup
   phục hồi được; xác nhận hash/body mới và rerun Booking regression. Nếu cần
   rollback function, dùng đúng definition cũ đã lưu trong migration riêng và
   đánh giá lại rủi ro; không sửa lại 001/002 đã từng được triển khai.

## Dữ liệu cũ, khóa, rollback và phục hồi

004 không DROP/TRUNCATE/DELETE/UPDATE bảng ứng dụng; chỉ mở rộng payment, tạo hai
bảng audit/marker. Legacy nhận false ở needs_reconciliation, cột khác NULL; CHECK
sandbox không áp lên provider khác. Nếu có sandbox reserved trước đó thì abort,
không đoán/backfill ID/url/TTL. Chưa đọc bảng người dùng để đếm các hàng này; DO
preflight sẽ kiểm tra khi được phép áp dụng trong tương lai.

Các DDL/index/marker cùng BEGIN/COMMIT; lỗi trước commit khiến transaction abort,
ROLLBACK hoặc đóng session trả schema về trước đó. Không dùng CREATE INDEX
CONCURRENTLY trong transaction. Có lock timeout 2s, statement timeout 30s và
idle-in-transaction timeout 30s. Đây là timeout từng statement, không là deadline
toàn migration. ALTER/CHECK/index vẫn có thể scan bảng và chặn đọc/ghi trong thời
gian giữ ACCESS EXCLUSIVE; không coi đây là online migration không ảnh hưởng.

Sau COMMIT hoặc khi đã có receipt: không chạy down DROP bảng/cột/audit. Dừng tạo
checkout mới bằng kế hoạch vẫn tiếp nhận IPN đang chờ, ưu tiên sửa tiến (roll
forward). Full restore cần backup nhất quán và WAL/PITR nếu có, phục hồi thử vào
server độc lập trước, kiểm tra các Payment phát sinh sau backup và đối soát với
VNPay; restore snapshot cũ có thể mất receipt mới. Không tắt toàn bộ IPN khi còn
giao dịch đang chờ mà không có kế hoạch vận hành.

Proposal là one-shot và cố ý fail nếu đã có object/cột/version; không dùng IF NOT
EXISTS để che partial deployment. Sau lỗi phải rollback/kiểm tra catalog trước
retry. Không có khả năng rollback sau commit đã được kiểm thử trong giai đoạn này.

## Kế hoạch kiểm thử độc lập — chỉ đề xuất, chưa tạo database

1. Sau quyền mới, dùng PostgreSQL 16.15 container/server riêng, volume riêng,
   loopback port riêng; không dùng Tailscale/proxy trỏ tới shared. Chặn network
   từ test tới host nhóm, không dùng connection appsettings hay secret production.
   Kiểm tra server identity và database prefix/test marker, không chỉ localhost
   (localhost vẫn có thể là tunnel). Runtime role không có CREATEDB/CREATEROLE.
2. Hai bộ fixture: fresh consolidated 001 (không chạy 002 mù quáng); và schema
   snapshot sanitized mô phỏng shared với quote guard 002. Không dump dữ liệu/PII
   nhóm. Seed synthetic owner/customer/vehicle/booking + legacy provider payments.
3. Snapshot schema/row manifest trước migration. Apply 004 chỉ trên DB test;
   đối chiếu đủ types, defaults, CHECK, FK, keys, index predicates/validity, marker,
   grants. Legacy row values/Booking/Wallet phải không đổi. Rerun phải fail rõ ràng.
4. Chèn lỗi migration ở bản copy test trước COMMIT, thiếu baseline column/unique,
   có object/cột trùng hoặc reserved provider. Xác minh rollback toàn bộ DDL/marker,
   không sót index/cột. Test lock timeout với connection giữ đúng Payment test.
5. Chạy Npgsql repository thật: Create/replay cùng key, key khác, hai request đồng
   thời; Pending/Processing/Failed/Cancelled/Succeeded/expired/needs_reconciliation;
   amount từ DB và quote không đổi, JWT/owner/Account/Role authorization.
6. IPN cùng fingerprint lặp/đồng thời; hai reference dùng một transaction number;
   success rồi failure; failure rồi late success; multiple gateway receipts;
   callback sai amount/ref/signature; khác provider. Kiểm tra số audit/disposition,
   reason, paid_at, không Confirmed lần hai và không mở attempt mới khi chưa rõ.
7. Fault injection sau INSERT audit, sau UPDATE Payment, trước UPDATE Booking và
   trước COMMIT: cả ba thay đổi cùng rollback. Dùng connection/barrier riêng, lock
   đúng UUID synthetic, timeout; xác minh Vehicle→Booking→Payment lock order và
   contention với Booking Approval/Account suspension, không dùng whole-table
   row locks. Seed chưa commit không thể thấy từ connection cạnh tranh.
8. Kiểm tra quote guard repair riêng, Booking regression, tài khoản app có quyền
   đủ trên bảng mới nhưng không sửa/xóa audit. Rerun thực tế có PG16 trước khi
   dựa vào kết quả parser/unit. Restore drill backup vào server test thứ hai và
   đối chiếu manifest/constraints/receipt/audit trước-sau.
9. Fixture hiện có DisposablePostgresDatabase chỉ apply 001, seed và CREATE/DROP
   database; chưa hỗ trợ 004/audit/grants/identity marker đầy đủ. Không bật lại
   fixture đó trên shared, không chỉ đổi connection string để chạy Payment.

## Kiểm tra đợt này và điều kiện E2E

- Parser ngoại tuyến pglast v8.7: 17 SQL statements và hai body DO/PLpgSQL parse
  thành công. Không kết nối DB, không thực thi SQL. Parser không thay thế kiểm tra
  name/type resolution, CHECK/constraint hoặc transaction runtime PostgreSQL 16.15.
- dotnet build Release: thành công, 0 warning/error.
- Toàn bộ suite với các biến enable DB bị xóa: 156 passed, 0 failed, 13 skipped
  (10 nhóm ghi local, 3 shared opt-in). Hai metadata test được chạy riêng có cấu
  hình read-only: 2 passed, 0 failed. Không cộng các lượt chạy thành một suite.
- git diff --check: không có lỗi whitespace. Không sửa/chạy lại Frontend vì không
  thay đổi Frontend; các con số 63/36 trước đó không được coi là kiểm thử lại ở 2E.1.
- Database: 0 bản ghi nghiệp vụ được tạo/sửa/xóa, 0 DDL/migration được thực thi.

Chưa đủ điều kiện sandbox end-to-end. Cần nhóm duyệt schema/proposal và sửa drift
quote guard riêng; có backup restore được; chấp thuận môi trường PG độc lập; pass
migration/repository/concurrency/fault-injection tests; chốt writer/TTL/đối soát,
quyền DB và schema verification; merchant sandbox/secret bảo mật; HTTPS IPN/Return,
FrontendResultUrl/CORS/SPA routing đã kiểm tra trên hosting. Chỉ bật sandbox ở môi
trường đã duyệt sau các gate này; Payment production tiếp tục tắt.
