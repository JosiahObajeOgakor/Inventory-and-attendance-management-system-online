using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Staff;

public sealed record EmployeeDto(int Id, string FullName, string Position, string? Phone, DateOnly StartedOn, decimal MonthlySalary, bool IsActive, decimal OpenLoanBalance, int PayrollMonths, int Loans);
public sealed record EmployeeInput(string FullName, string Position, string? Phone, DateOnly? StartedOn, decimal MonthlySalary);
public sealed record PayrollRowDto(int Id, int EmployeeId, string FullName, string Position, int PeriodYear, int PeriodMonth, decimal SalaryAmount, decimal LoanDeduction, decimal NetPay, bool Paid, DateOnly? PaidDate, string? Note);
public sealed record PayrollEditInput(decimal SalaryAmount, decimal LoanDeduction, string? Note);
public sealed record PaidResult(string FullName, string Period, decimal NetPay, bool AlreadyPaid);
public sealed record RepaymentDto(DateOnly PaidDate, decimal Amount, string? Note);
public sealed record LoanDto(int Id, DateOnly LoanDate, decimal Principal, decimal Balance, bool Closed, string? Note, IReadOnlyList<RepaymentDto> Repayments);

public sealed class EmployeeInputValidator : AbstractValidator<EmployeeInput>
{
    public EmployeeInputValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Position).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.MonthlySalary).GreaterThanOrEqualTo(0);
    }
}

/// <summary>Employees, monthly payroll and staff loans (rules R1–R3), ported from Payroll.vb / ucEmployees.vb.</summary>
public sealed class PayrollService(IBusinessDbContext db, TransactionRunner tx, IClock clock)
{
    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static string Period(int year, int month) => $"{MonthNames[month - 1]} {year}";
    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private AuditLog Audit(CurrentUser u, string action, string entity, int id, string detail) =>
        new() { UserId = u.Id, UserName = u.FullName, Action = action, Entity = entity, EntityId = id.ToString(), At = clock.UtcNow, Detail = detail };

    // ---------------------------------------------------------------- employees
    public async Task<List<EmployeeDto>> EmployeesAsync(CancellationToken ct = default)
    {
        var emps = await db.Employees.AsNoTracking().OrderByDescending(e => e.IsActive).ThenBy(e => e.FullName).ToListAsync(ct);
        var loans = await db.EmployeeLoans.AsNoTracking().GroupBy(l => l.EmployeeId).Select(g => new { g.Key, Open = g.Where(l => !l.Closed).Sum(l => l.Balance), N = g.Count() }).ToDictionaryAsync(x => x.Key, ct);
        var months = await db.EmployeeMonthlies.AsNoTracking().GroupBy(m => m.EmployeeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return emps.Select(e => new EmployeeDto(e.Id, e.FullName, e.Position, e.Phone, e.StartedOn, e.MonthlySalary, e.IsActive,
            loans.TryGetValue(e.Id, out var l) ? l.Open : 0, months.GetValueOrDefault(e.Id), loans.TryGetValue(e.Id, out var l2) ? l2.N : 0)).ToList();
    }

    public async Task<int> CreateEmployeeAsync(EmployeeInput i, CurrentUser user, CancellationToken ct = default)
    {
        await new EmployeeInputValidator().ValidateAndThrowAsync(i, ct);
        var e = new Employee { FullName = i.FullName.Trim(), Position = i.Position.Trim(), Phone = N(i.Phone), StartedOn = i.StartedOn ?? clock.BusinessToday, MonthlySalary = i.MonthlySalary };
        db.Employees.Add(e);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(Audit(user, "EMPLOYEE_CREATED", "Employee", e.Id, e.FullName));
        await db.SaveChangesAsync(ct);
        return e.Id;
    }

    public async Task UpdateEmployeeAsync(int id, EmployeeInput i, CurrentUser user, CancellationToken ct = default)
    {
        await new EmployeeInputValidator().ValidateAndThrowAsync(i, ct);
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Employee");
        e.FullName = i.FullName.Trim(); e.Position = i.Position.Trim(); e.Phone = N(i.Phone); e.MonthlySalary = i.MonthlySalary;
        if (i.StartedOn.HasValue) e.StartedOn = i.StartedOn.Value;
        db.AuditLogs.Add(Audit(user, "EMPLOYEE_UPDATED", "Employee", id, e.FullName));
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(int id, bool active, CurrentUser user, CancellationToken ct = default)
    {
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Employee");
        e.IsActive = active;
        db.AuditLogs.Add(Audit(user, active ? "EMPLOYEE_REACTIVATED" : "EMPLOYEE_DEACTIVATED", "Employee", id, e.FullName));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The desktop app deleted an employee together with all their payroll and loan history. Payroll records are financial
    /// history, so here an employee who has any is deactivated instead; only one with no records can be deleted.
    /// </summary>
    public async Task<bool> DeleteEmployeeAsync(int id, CurrentUser user, CancellationToken ct = default)
    {
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Employee");
        if (await db.EmployeeMonthlies.AnyAsync(m => m.EmployeeId == id, ct) || await db.EmployeeLoans.AnyAsync(l => l.EmployeeId == id, ct))
            throw new BusinessRuleException($"{e.FullName} has payroll or loan records, which are kept for the accounts. Switch them off instead of deleting.");
        db.Employees.Remove(e);
        db.AuditLogs.Add(Audit(user, "EMPLOYEE_DELETED", "Employee", id, e.FullName));
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------- monthly payroll
    public async Task<List<PayrollRowDto>> MonthAsync(int year, int month, CancellationToken ct = default)
    {
        var rows = await (from m in db.EmployeeMonthlies.AsNoTracking() join e in db.Employees.AsNoTracking() on m.EmployeeId equals e.Id
                          where m.PeriodYear == year && m.PeriodMonth == month orderby e.FullName select new { m, e.FullName }).ToListAsync(ct);
        return rows.Select(r => Row(r.m, r.FullName)).ToList();
    }

    private static PayrollRowDto Row(EmployeeMonthly m, string name) =>
        new(m.Id, m.EmployeeId, name, m.Position, m.PeriodYear, m.PeriodMonth, m.SalaryAmount, m.LoanDeduction, m.SalaryAmount - m.LoanDeduction, m.Paid, m.PaidDate, m.Note);

    /// <summary>
    /// "Generate month" (R1): for each active employee with no row for the period — salary is their last paid amount, else their normal
    /// salary; the loan deduction is a sixth of their open loan balance (capped at it). Re-runnable: existing rows are skipped.
    /// </summary>
    public async Task<int> GenerateMonthAsync(int year, int month, CurrentUser user, CancellationToken ct = default)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100) throw new BusinessRuleException("Choose a valid month.");
        return await tx.RunAsync(async inner =>
        {
            var have = await db.EmployeeMonthlies.Where(m => m.PeriodYear == year && m.PeriodMonth == month).Select(m => m.EmployeeId).ToListAsync(inner);
            var emps = await db.Employees.Where(e => e.IsActive && !have.Contains(e.Id)).ToListAsync(inner);
            var added = 0;
            foreach (var e in emps)
            {
                var last = await db.EmployeeMonthlies.Where(m => m.EmployeeId == e.Id).OrderByDescending(m => m.PeriodYear).ThenByDescending(m => m.PeriodMonth)
                    .Select(m => (decimal?)m.SalaryAmount).FirstOrDefaultAsync(inner);
                var open = await db.EmployeeLoans.Where(l => l.EmployeeId == e.Id && !l.Closed).SumAsync(l => (decimal?)l.Balance, inner) ?? 0;
                var instalment = Math.Min(open, Money.Round(open / 6m));
                db.EmployeeMonthlies.Add(new EmployeeMonthly { EmployeeId = e.Id, PeriodYear = year, PeriodMonth = month, Position = e.Position, SalaryAmount = last ?? e.MonthlySalary, LoanDeduction = instalment });
                added++;
            }
            if (added > 0) db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PAYROLL_GENERATED", Entity = "Payroll", At = clock.UtcNow, Detail = $"{Period(year, month)}: {added} employee(s)" });
            await db.SaveChangesAsync(inner);
            return added;
        }, ct);
    }

    public async Task EditRowAsync(int id, PayrollEditInput i, CurrentUser user, CancellationToken ct = default)
    {
        if (i.SalaryAmount < 0 || i.LoanDeduction < 0) throw new BusinessRuleException("Amounts can't be negative.");
        if (i.LoanDeduction > i.SalaryAmount) throw new BusinessRuleException("The loan deduction can't be more than the salary.");
        var m = await db.EmployeeMonthlies.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Payroll row");
        if (m.Paid) throw new BusinessRuleException("This row has been paid and can no longer be changed.");
        var open = await db.EmployeeLoans.Where(l => l.EmployeeId == m.EmployeeId && !l.Closed).SumAsync(l => (decimal?)l.Balance, ct) ?? 0;
        if (i.LoanDeduction > open) throw new BusinessRuleException($"They only owe ₦{open:N2} on open loans.");
        m.SalaryAmount = i.SalaryAmount; m.LoanDeduction = i.LoanDeduction; m.Note = N(i.Note);
        db.AuditLogs.Add(Audit(user, "PAYROLL_EDITED", "EmployeeMonthly", id, $"{i.SalaryAmount:N2} / deduct {i.LoanDeduction:N2}"));
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRowAsync(int id, CurrentUser user, CancellationToken ct = default)
    {
        var m = await db.EmployeeMonthlies.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Payroll row");
        if (m.Paid) throw new BusinessRuleException("A paid row is part of the accounts and can't be removed.");
        db.EmployeeMonthlies.Remove(m);
        db.AuditLogs.Add(Audit(user, "PAYROLL_ROW_DELETED", "EmployeeMonthly", id, Period(m.PeriodYear, m.PeriodMonth)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One transaction: mark paid (guarded, so a second click loses the race), apply the loan deduction to open loans oldest first,
    /// and post the NET pay as a Salaries expense dated today (R2).
    /// </summary>
    public Task<PaidResult> MarkPaidAsync(int id, CurrentUser user, CancellationToken ct = default) => tx.RunAsync(inner => PayOneAsync(id, user, inner), ct);

    public Task<int> MarkAllPaidAsync(int year, int month, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync(async inner =>
        {
            var ids = await db.EmployeeMonthlies.Where(m => m.PeriodYear == year && m.PeriodMonth == month && !m.Paid).OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(inner);
            var paid = 0;
            foreach (var id in ids) if (!(await PayOneAsync(id, user, inner)).AlreadyPaid) paid++;
            return paid;
        }, ct);

    private async Task<PaidResult> PayOneAsync(int id, CurrentUser user, CancellationToken ct)
    {
        var m = await db.EmployeeMonthlies.FromSqlInterpolated($"SELECT * FROM employee_monthly WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Payroll row");
        var e = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == m.EmployeeId, ct);
        var period = Period(m.PeriodYear, m.PeriodMonth);
        var net = m.SalaryAmount - m.LoanDeduction;
        if (m.Paid) return new PaidResult(e.FullName, period, net, true);

        m.Paid = true; m.PaidDate = clock.BusinessToday;
        if (m.LoanDeduction > 0) await ApplyLoanRepaymentAsync(m.EmployeeId, m.LoanDeduction, $"Payroll deduction {period}", ct);
        db.Expenses.Add(new Expense { Category = "Salaries", ExpenseDate = clock.BusinessToday, Amount = net, Note = $"{e.FullName} — {period}", CreatedByUserId = user.Id });
        db.AuditLogs.Add(Audit(user, "PAYROLL_PAID", "EmployeeMonthly", id, $"{e.FullName} {period} net {net:N2}"));
        await db.SaveChangesAsync(ct);
        return new PaidResult(e.FullName, period, net, false);
    }

    /// <summary>Reduces the employee's oldest open loan(s) by <paramref name="amount"/>, logging a repayment against each.</summary>
    private async Task ApplyLoanRepaymentAsync(int employeeId, decimal amount, string note, CancellationToken ct)
    {
        var remaining = amount;
        var loans = await db.EmployeeLoans.FromSqlInterpolated($"SELECT * FROM employee_loans WHERE EmployeeId = {employeeId} AND Closed = 0 ORDER BY LoanDate, Id FOR UPDATE").ToListAsync(ct);
        foreach (var l in loans)
        {
            if (remaining <= 0) break;
            var pay = Math.Min(l.Balance, remaining);
            db.LoanRepayments.Add(new LoanRepayment { LoanId = l.Id, PaidDate = clock.BusinessToday, Amount = pay, Note = note });
            l.Balance -= pay; l.Closed = l.Balance <= 0;
            remaining -= pay;
        }
    }

    public async Task<List<PayrollRowDto>> HistoryAsync(int employeeId, CancellationToken ct = default)
    {
        var name = await db.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.FullName).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Employee");
        var rows = await db.EmployeeMonthlies.AsNoTracking().Where(m => m.EmployeeId == employeeId).OrderByDescending(m => m.PeriodYear).ThenByDescending(m => m.PeriodMonth).ToListAsync(ct);
        return rows.Select(m => Row(m, name)).ToList();
    }

    // ---------------------------------------------------------------- loans
    public async Task<List<LoanDto>> LoansAsync(int employeeId, CancellationToken ct = default)
    {
        var loans = await db.EmployeeLoans.AsNoTracking().Where(l => l.EmployeeId == employeeId).OrderByDescending(l => l.LoanDate).ThenByDescending(l => l.Id).ToListAsync(ct);
        var ids = loans.Select(l => l.Id).ToList();
        var reps = await db.LoanRepayments.AsNoTracking().Where(r => ids.Contains(r.LoanId)).OrderBy(r => r.PaidDate).ToListAsync(ct);
        return loans.Select(l => new LoanDto(l.Id, l.LoanDate, l.Principal, l.Balance, l.Closed, l.Note,
            reps.Where(r => r.LoanId == l.Id).Select(r => new RepaymentDto(r.PaidDate, r.Amount, r.Note)).ToList())).ToList();
    }

    public async Task<int> AddLoanAsync(int employeeId, decimal amount, string? note, CurrentUser user, CancellationToken ct = default)
    {
        if (amount <= 0) throw new BusinessRuleException("Enter a loan amount greater than zero.");
        if (!await db.Employees.AnyAsync(e => e.Id == employeeId, ct)) throw new NotFoundException("Employee");
        var l = new EmployeeLoan { EmployeeId = employeeId, LoanDate = clock.BusinessToday, Principal = amount, Balance = amount, Note = N(note) };
        db.EmployeeLoans.Add(l);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(Audit(user, "LOAN_ADDED", "EmployeeLoan", l.Id, $"{amount:N2}"));
        await db.SaveChangesAsync(ct);
        return l.Id;
    }

    /// <summary>A manual repayment outside payroll (rule R3), capped at what is still owed.</summary>
    public Task RepayLoanAsync(int loanId, decimal amount, string? note, CurrentUser user, CancellationToken ct = default) =>
        tx.RunAsync<bool>(async inner =>
        {
            if (amount <= 0) throw new BusinessRuleException("Enter an amount greater than zero.");
            var l = await db.EmployeeLoans.FromSqlInterpolated($"SELECT * FROM employee_loans WHERE Id = {loanId} FOR UPDATE").SingleOrDefaultAsync(inner) ?? throw new NotFoundException("Loan");
            if (l.Closed) throw new BusinessRuleException("This loan is already fully repaid.");
            if (amount > l.Balance) throw new BusinessRuleException($"That is more than the ₦{l.Balance:N2} still owed.");
            db.LoanRepayments.Add(new LoanRepayment { LoanId = l.Id, PaidDate = clock.BusinessToday, Amount = amount, Note = N(note) ?? "Repayment" });
            l.Balance -= amount; l.Closed = l.Balance <= 0;
            db.AuditLogs.Add(Audit(user, "LOAN_REPAID", "EmployeeLoan", loanId, $"{amount:N2}"));
            await db.SaveChangesAsync(inner);
            return true;
        }, ct);

    /// <summary>The desktop app also deleted a loan's repayment history; here a loan that has been repaid against is kept.</summary>
    public async Task DeleteLoanAsync(int loanId, CurrentUser user, CancellationToken ct = default)
    {
        var l = await db.EmployeeLoans.SingleOrDefaultAsync(x => x.Id == loanId, ct) ?? throw new NotFoundException("Loan");
        if (await db.LoanRepayments.AnyAsync(r => r.LoanId == loanId, ct)) throw new BusinessRuleException("This loan has repayments recorded and is kept for the accounts.");
        db.EmployeeLoans.Remove(l);
        db.AuditLogs.Add(Audit(user, "LOAN_DELETED", "EmployeeLoan", loanId, $"{l.Principal:N2}"));
        await db.SaveChangesAsync(ct);
    }
}
