# Metadata đăng ký xe (POST /api/vehicles/registrations)

Thư mục này chứa bộ dữ liệu mẫu để gửi thử API đăng ký xe mà không bị lỗi 400.

## Nội dung

| File | Mô tả |
|---|---|
| `metadata.json` | JSON metadata chuẩn, đã được test thành công (HTTP 201) |
| `metadata.schema.json` | JSON Schema mô tả toàn bộ field + ràng buộc |
| `car-sample-1.jpg` | Ảnh xe mẫu 1 (1280×860) |
| `car-sample-2.jpg` | Ảnh xe mẫu 2 |
| `car-sample-3.jpg` | Ảnh xe mẫu 3 |

## Gửi thủ công (Postman / Swagger / Insomnia)

Request phải là `multipart/form-data`:

| Field | Kiểu | Ghi chú |
|---|---|---|
| `metadata` | **Text** | Dán nguyên nội dung `metadata.json`. **Không** đổi type sang File/Blob. |
| `vehicleImages` | **File** | Chọn 1–8 ảnh, cùng tên field `vehicleImages`. |

Trong Postman: chọn tab **Body → form-data**, thêm row `metadata` với type **Text** (không phải File),
rồi thêm 1–8 row `vehicleImages` với type **File**.

> ⚠️ **Nguyên nhân phổ biến nhất gây 400**: đặt `metadata` là type **File**.
> Khi đó ASP.NET bind nó thành `IFormFile` nên `[FromForm] string metadata` nhận `null`
> và trả về `400 {"metadata":["The metadata field is required."]}`.
> Backend đã được sửa để chấp nhận cả hai kiểu, nhưng nên gửi dạng **Text** cho chắc chắn.

Đừng tự đặt header `Content-Type: multipart/form-data` — client phải tự thêm `boundary`.
Nếu thiếu boundary sẽ ra `400 {"":["Failed to read the request form. Missing content-type boundary."]}`.

## Gửi bằng curl

```bash
curl -X POST "https://bonbon-api.kitsuracloud.com/api/vehicles/registrations" \
  -H "Authorization: Bearer <ACCESS_TOKEN>" \
  -F "metadata=<metadata.json;type=application/json" \
  -F "vehicleImages=@car-sample-1.jpg" \
  -F "vehicleImages=@car-sample-2.jpg" \
  -F "vehicleImages=@car-sample-3.jpg"
```

Lưu ý cú pháp `metadata=<metadata.json` (dấu `<` nghĩa là đọc nội dung file làm giá trị field).
Nếu dùng `metadata=@metadata.json` thì curl sẽ gửi nó thành **file part** → dễ gặp lỗi 400.

## Gửi bằng JavaScript (fetch)

```js
const form = new FormData();
form.append('metadata', JSON.stringify(metadata)); // chuỗi, không bọc Blob
form.append('vehicleImages', file1);
form.append('vehicleImages', file2);

await fetch('/api/vehicles/registrations', {
  method: 'POST',
  headers: { Authorization: `Bearer ${token}` }, // không set Content-Type
  body: form,
});
```

## Quy tắc của metadata

Xem chi tiết trong `metadata.schema.json`. Tóm tắt:

- Bắt buộc: `plateNumber`, `brand`, `model`, `productionYear`, `seats`, `transmission`,
  `fuelType`, `bodyType`, `description` (≥ 20 ký tự), `pickupAddress` (≥ 10 ký tự),
  `hourPrice` (≥ 50.000), `dayPrice` (≥ 300.000)
- Enum đúng chính tả: `Manual`/`Automatic`, `Gasoline`/`Diesel`/`Electric`/`Hybrid`,
  `Sedan`/`SUV`/`MPV`/`Hatchback`
- `weeklyDiscountEnabled = true` → bắt buộc `weeklyDiscountPercent` trong khoảng 5–30
- `mileageLimitEnabled = true` → bắt buộc `dailyMileageLimitKm` (100–800) và
  `overageFeePerKm` (1.000–10.000)
- `features`: tối đa 20 mục, không được rỗng

## Mã lỗi thường gặp

| Mã lỗi | Nguyên nhân |
|---|---|
| `The metadata field is required.` | `metadata` gửi thành file part thay vì text |
| `Missing content-type boundary.` | Tự set `Content-Type: multipart/form-data` không kèm boundary |
| `INVALID_IMAGE_COUNT` | Thiếu ảnh, hoặc sai tên field (phải là `vehicleImages`), hoặc > 8 ảnh |
| `INVALID_METADATA` | JSON sai cú pháp, hoặc giá trị vi phạm ràng buộc |
| `INVALID_VEHICLE_INFO` | `description` < 20 ký tự, `pickupAddress` < 10 ký tự, `seats` ngoài 4–7, `productionYear` ngoài 1990–2100 |
| `INVALID_PRICE` | `hourPrice` < 50.000 hoặc `dayPrice` < 300.000 |
| `INVALID_TRANSMISSION` / `INVALID_FUEL_TYPE` / `INVALID_BODY_TYPE` | Enum sai chính tả |
| `INVALID_WEEKLY_DISCOUNT` | Bật giảm giá tuần nhưng thiếu `weeklyDiscountPercent` hoặc ngoài 5–30 |
| `INVALID_MILEAGE_POLICY` | Bật giới hạn km nhưng thiếu `dailyMileageLimitKm` / `overageFeePerKm` |
| `PLATE_ALREADY_EXISTS` | Biển số đã được đăng ký |
