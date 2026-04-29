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

    // GET /api/appointments
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
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
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

    // GET /api/appointments/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<AppointmentDetailsDto>> GetAppointmentById(int id)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                a.InternalNotes,
                a.CreatedAt,
                p.FirstName,
                p.LastName,
                p.Email,
                p.PhoneNumber,
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
            return NotFound();

        var result = new AppointmentDetailsDto
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
        };

        return Ok(result);
    }

    // POST /api/appointments
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

        // Check patient
        await using (var checkPatient = new SqlCommand("""
            SELECT COUNT(1) FROM Patients WHERE IdPatient = @Id AND IsActive = 1
        """, connection))
        {
            checkPatient.Parameters.Add("@Id", SqlDbType.Int).Value = request.IdPatient;

            var exists = (int)await checkPatient.ExecuteScalarAsync();
            if (exists == 0)
                return BadRequest("Patient does not exist or is inactive");
        }

        // Check doctor
        await using (var checkDoctor = new SqlCommand("""
            SELECT COUNT(1) FROM Doctors WHERE IdDoctor = @Id AND IsActive = 1
        """, connection))
        {
            checkDoctor.Parameters.Add("@Id", SqlDbType.Int).Value = request.IdDoctor;

            var exists = (int)await checkDoctor.ExecuteScalarAsync();
            if (exists == 0)
                return BadRequest("Doctor does not exist or is inactive");
        }

        // Check conflict
        await using (var checkConflict = new SqlCommand("""
            SELECT COUNT(1)
            FROM Appointments
            WHERE IdDoctor = @DoctorId
              AND AppointmentDate = @Date
        """, connection))
        {
            checkConflict.Parameters.Add("@DoctorId", SqlDbType.Int).Value = request.IdDoctor;
            checkConflict.Parameters.Add("@Date", SqlDbType.DateTime2).Value = request.AppointmentDate;

            var conflict = (int)await checkConflict.ExecuteScalarAsync();
            if (conflict > 0)
                return Conflict("Doctor already has appointment at this time");
        }

        // Insert
        await using (var insert = new SqlCommand("""
            INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            VALUES (@PatientId, @DoctorId, @Date, 'Scheduled', @Reason);
        """, connection))
        {
            insert.Parameters.Add("@PatientId", SqlDbType.Int).Value = request.IdPatient;
            insert.Parameters.Add("@DoctorId", SqlDbType.Int).Value = request.IdDoctor;
            insert.Parameters.Add("@Date", SqlDbType.DateTime2).Value = request.AppointmentDate;
            insert.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;

            await insert.ExecuteNonQueryAsync();
        }

        return StatusCode(201);
    }
}