# HCM module

HCM owns company-scoped employee records, leave requests, attendance, benefit
enrollments, completed training records, and performance reviews in the `hcm`
schema. API routes are under `/api/v1/hcm`; employee and leave/attendance
operations use `hcm.read`, `hcm.manage`, and `hcm.leave.approve`. Benefit,
training, and confidential performance-review records have distinct read and
manage permissions.

Employee records retain work contact details and employment dates. Leave
requests follow a pending-to-approved/rejected decision flow. Attendance is
captured with UTC check-in/check-out timestamps and one record per employee and
work date. Employee lifecycle currently supports hiring, deactivation, and
reactivation; inactive employees cannot submit leave or attendance records.
Benefit records describe enrollments only; training records capture
completion and optional certificate/expiry data. Performance reviews are
confidential narrative records with separately guarded access.

Payroll configuration APIs persist immutable jurisdiction rule versions and
effective-dated employee compensation, including named allowances and
deductions. The deterministic calculation engine consumes explicit rules for
attendance proration, overtime, and progressive deductions; it does not embed
country-specific rates. Payroll run creation is not yet connected to these saved
inputs or finalized attendance. Manual amount entry has been removed, and
approval requires a reproducible calculation snapshot. General Ledger posting,
payslips, and bank disbursement remain future workflow stages.
