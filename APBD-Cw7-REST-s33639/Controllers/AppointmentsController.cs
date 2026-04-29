using System.Data;
using APBD_Cw7_REST_s33639.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace APBD_Cw7_REST_s33639.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AppointmentsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AppointmentListDto>>> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {
        var appointments = new List<AppointmentListDto>();
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                   p.FirstName + N' ' + p.LastName AS PatientFullName,
                   p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value =
            string.IsNullOrWhiteSpace(status) ? DBNull.Value : status;

        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value =
            string.IsNullOrWhiteSpace(patientLastName) ? DBNull.Value : patientLastName;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
            });
        }

        return Ok(appointments);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AppointmentDetailsDto>> GetAppointmentById(int id)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                   a.InternalNotes, a.CreatedAt,
                   p.FirstName, p.LastName, p.Email, p.PhoneNumber,
                   d.FirstName AS DoctorFirstName,
                   d.LastName AS DoctorLastName,
                   d.LicenseNumber
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            WHERE a.IdAppointment = @Id;
            """, connection);

        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return NotFound("Appointment not found");

        return Ok(new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
            AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            Reason = reader.GetString(reader.GetOrdinal("Reason")),
            InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes"))
                ? null
                : reader.GetString(reader.GetOrdinal("InternalNotes")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            PatientFirstName = reader.GetString(reader.GetOrdinal("FirstName")),
            PatientLastName = reader.GetString(reader.GetOrdinal("LastName")),
            PatientEmail = reader.GetString(reader.GetOrdinal("Email")),
            PatientPhoneNumber = reader.GetString(reader.GetOrdinal("PhoneNumber")),
            DoctorFirstName = reader.GetString(reader.GetOrdinal("DoctorFirstName")),
            DoctorLastName = reader.GetString(reader.GetOrdinal("DoctorLastName")),
            DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("LicenseNumber"))
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate <= DateTime.Now)
            return BadRequest("Appointment date cannot be in the past");

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest("Reason must be between 1 and 250 characters");

        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        if (!await ActivePatientExists(connection, request.IdPatient))
            return BadRequest("Patient does not exist or is inactive");

        if (!await ActiveDoctorExists(connection, request.IdDoctor))
            return BadRequest("Doctor does not exist or is inactive");

        if (await DoctorHasAppointmentConflict(connection, request.IdDoctor, request.AppointmentDate, null))
            return Conflict("Doctor already has appointment at this time");

        await using var insert = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, 'Scheduled', @Reason);

            SELECT CAST(SCOPE_IDENTITY() AS int);
            """, connection);

        insert.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        insert.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        insert.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        insert.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;

        var newId = (int)await insert.ExecuteScalarAsync();

        return CreatedAtAction(nameof(GetAppointmentById), new { id = newId }, new { idAppointment = newId });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateAppointment(int id, [FromBody] UpdateAppointmentRequestDto request)
    {
        var allowedStatuses = new[] { "Scheduled", "Completed", "Cancelled" };

        if (!allowedStatuses.Contains(request.Status))
            return BadRequest("Status must be one of: Scheduled, Completed, Cancelled");

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest("Reason must be between 1 and 250 characters");

        if (request.InternalNotes is not null && request.InternalNotes.Length > 500)
            return BadRequest("Internal notes cannot be longer than 500 characters");

        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var checkAppointment = new SqlCommand("""
            SELECT Status, AppointmentDate
            FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        checkAppointment.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

        string currentStatus;
        DateTime currentDate;

        await using (var reader = await checkAppointment.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return NotFound("Appointment not found");

            currentStatus = reader.GetString(reader.GetOrdinal("Status"));
            currentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate"));
        }

        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
            return Conflict("Cannot change appointment date when appointment is completed");

        if (!await ActivePatientExists(connection, request.IdPatient))
            return BadRequest("Patient does not exist or is inactive");

        if (!await ActiveDoctorExists(connection, request.IdDoctor))
            return BadRequest("Doctor does not exist or is inactive");

        if (await DoctorHasAppointmentConflict(connection, request.IdDoctor, request.AppointmentDate, id))
            return Conflict("Doctor already has another appointment at this time");

        await using var update = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        update.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        update.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        update.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        update.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
        update.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;
        update.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value =
            request.InternalNotes is null ? DBNull.Value : request.InternalNotes;
        update.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

        await update.ExecuteNonQueryAsync();

        return Ok();
    }

    private static async Task<bool> ActivePatientExists(SqlConnection connection, int idPatient)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1)
            FROM dbo.Patients
            WHERE IdPatient = @IdPatient AND IsActive = 1;
            """, connection);

        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = idPatient;

        var result = (int)await command.ExecuteScalarAsync();
        return result > 0;
    }

    private static async Task<bool> ActiveDoctorExists(SqlConnection connection, int idDoctor)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1)
            FROM dbo.Doctors
            WHERE IdDoctor = @IdDoctor AND IsActive = 1;
            """, connection);

        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;

        var result = (int)await command.ExecuteScalarAsync();
        return result > 0;
    }

    private static async Task<bool> DoctorHasAppointmentConflict(
        SqlConnection connection,
        int idDoctor,
        DateTime appointmentDate,
        int? excludedAppointmentId)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1)
            FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND (@ExcludedAppointmentId IS NULL OR IdAppointment <> @ExcludedAppointmentId);
            """, connection);

        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;
        command.Parameters.Add("@ExcludedAppointmentId", SqlDbType.Int).Value =
            excludedAppointmentId is null ? DBNull.Value : excludedAppointmentId;

        var result = (int)await command.ExecuteScalarAsync();
        return result > 0;
    }
}