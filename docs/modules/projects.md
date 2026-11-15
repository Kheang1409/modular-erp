# Projects module

Projects owns company-scoped project records, milestones, tasks, and time entries
in the `projects` schema. The built-in Projects page and API routes under
`/api/v1/projects` require `projects.read` or `projects.manage`.

Projects can be planned with dated milestones and tasks. Time entries record
work date, hours, description, and billable status with concurrency-protected
updates. Billable hours are only captured for later integration; this module
does not infer rates or taxes, create Accounting invoices, or change posted
financial records.
