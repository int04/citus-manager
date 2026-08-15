# Citus Manager

Control-plane ASP.NET Core cho nhiều self-hosted Citus database. UI thay lệnh thủ công cho inventory, add worker, rebalance, drain và remove worker với preflight, approval hai người, checkpoint, audit.

## Yêu cầu

- .NET SDK 10
- PostgreSQL riêng cho control DB
- Coordinator/worker đã cài PostgreSQL + Citus, network/TLS/auth sẵn
- Data Protection keyring bền vững, nằm ngoài control DB
- Prometheus/node_exporter tùy chọn

App không provision VM/container, không sửa firewall/`pg_hba.conf`, không xóa volume, không thay thế backup/PITR/HA.

## Cấu hình

Không commit secrets. Dùng environment variables hoặc secret manager:

```powershell
$env:ConnectionStrings__ControlDatabase='Host=control-db;Port=5432;Database=citus_manager;Username=citus_manager;Password=<SECRET>;SSL Mode=VerifyFull'
$env:Security__DataProtectionKeyPath='D:\protected\citus-manager-keys'
$env:Notifications__WebhookUrl='https://hooks.example/internal/...'
$env:Notifications__Smtp__Host='smtp.example'
$env:Notifications__Smtp__Username='<USER>'
$env:Notifications__Smtp__Password='<SECRET>'
```

Production: mount keyring trên encrypted persistent storage; backup/restore key cùng control DB. Mất key → không giải mã cluster credentials.

## Khởi tạo

```powershell
dotnet restore
dotnet ef database update
dotnet run
```

Mở `/Account/Setup`, tạo Admin đầu tiên; sau đó tạo Admin thứ hai trong **Users & roles**. Requester không thể tự approve operation.

Development có `Database:AutoCreateSchema=true`, app tự chạy migrations. Production mặc định tắt; apply migration trong controlled deployment.

## Roles

- `Viewer`: dashboard, topology, database explorer/SQL, metrics, activity, alerts.
- `Operator`: thêm profile, tạo operation plan, acknowledge alert, request cancel.
- `Admin`: quản lý user/profile, approve operation của người khác, audit.

## Database explorer

Mở từ **Dữ liệu bảng** trong cluster Details. Coordinator browser đọc logical table nên thấy dữ liệu toàn cluster. Nút **Dữ liệu node** kết nối trực tiếp node topology bằng credential cluster và chỉ đọc physical shard placements trên node đó.

SQL console chỉ chạy trên coordinator, cho mọi user đã đăng nhập và không giới hạn loại statement PostgreSQL. Mỗi lần chạy phải xác nhận; mặc định timeout 60 giây, tối đa 1.000 rows/result set và audit chỉ lưu SHA-256/metadata, không lưu SQL plaintext. Quyền thực tế vẫn do database role trong cluster profile quyết định.

## Safety invariants

- Capability scan theo database/version/function signature; feature thiếu → chặn.
- Mọi topology mutation: immutable plan → Admin khác approve → live preflight → runner.
- Một impact operation/cluster nhờ PostgreSQL advisory lock.
- Add worker không tự rebalance.
- Drain cancel không trả shards đã chuyển.
- Remove worker luôn kiểm tra `placements_left = 0`; khác 0 → tuyệt đối chặn.
- Worker mất và còn unique shard → `RecoveryRequired`, không giả vờ remove có thể phục hồi data.
- Password/token mã hóa; không trả qua API/log/audit. Activity không hiển thị SQL text/parameters.

## Monitoring

SQL collector mặc định 60 giây, raw retention 30 ngày. Theo dõi node active, metadata sync, placements, shard bytes, table count. Prometheus tùy chọn bổ sung target/CPU/RAM/filesystem aggregate. Alert xuất hiện in-app; webhook/SMTP retry tối đa năm lần.

Pairwise `citus_check_cluster_node_health()` không poll liên tục vì có thể mở nhiều connections; chạy qua runbook/diagnostic có kiểm soát.

## Kiểm tra

```powershell
dotnet build
dotnet test
dotnet list package --vulnerable --include-transitive
```

[CitusManager.http](CitusManager.http) chứa mẫu request cho mọi API chính. OpenAPI development: `/openapi/v1.json`.

## Phạm vi tiếp theo

Topology lifecycle, monitoring và database explorer đã hoạt động. Schema/table wizard nâng cao (distributed/reference table, shard-count, colocation, tenant/schema move) phải dùng cùng operation engine và chỉ bật sau staging rehearsal theo Citus version/workload thực tế.
