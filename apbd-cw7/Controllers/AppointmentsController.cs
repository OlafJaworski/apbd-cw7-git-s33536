using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
namespace apbd_cw7.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
                            ?? throw new InvalidOperationException("Connection string not found.");
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var appointments = new List<AppointmentListDto>();

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand("""
            SELECT 
                a.IdAppointment, 
                a.AppointmentDate, 
                a.Status, 
                a.Reason, 
                p.FirstName + ' ' + p.LastName AS PatientFullName, 
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value = (object?)patientLastName ?? DBNull.Value;

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto(
                reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                reader.GetString(reader.GetOrdinal("Status")),
                reader.GetString(reader.GetOrdinal("Reason")),
                reader.GetString(reader.GetOrdinal("PatientFullName")),
                reader.GetString(reader.GetOrdinal("PatientEmail"))
            ));
        }

        return Ok(appointments);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAppointmentDetails(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand("""
            SELECT 
                a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail, p.PhoneNumber,
                d.FirstName + ' ' + d.LastName AS DoctorFullName, d.LicenseNumber,
                s.Name AS SpecializationName
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
            JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
            JOIN dbo.Specializations s ON d.IdSpecialization = s.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return NotFound(new { Message = $"Appointment with ID {id} not found." });
        }

        var internalNotesOrdinal = reader.GetOrdinal("InternalNotes");
        var internalNotes = reader.IsDBNull(internalNotesOrdinal) ? null : reader.GetString(internalNotesOrdinal);

        var dto = new AppointmentDetailsDto(
            reader.GetInt32(reader.GetOrdinal("IdAppointment")),
            reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
            reader.GetString(reader.GetOrdinal("Status")),
            reader.GetString(reader.GetOrdinal("Reason")),
            internalNotes,
            reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            reader.GetString(reader.GetOrdinal("PatientFullName")),
            reader.GetString(reader.GetOrdinal("PatientEmail")),
            reader.GetString(reader.GetOrdinal("PhoneNumber")),
            reader.GetString(reader.GetOrdinal("DoctorFullName")),
            reader.GetString(reader.GetOrdinal("SpecializationName")),
            reader.GetString(reader.GetOrdinal("LicenseNumber"))
        );

        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate <= DateTime.UtcNow)
            return BadRequest(new { Message = "Appointment date must be in the future." });

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest(new { Message = "Reason cannot be empty and must be max 250 characters." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        if (!await CheckIfEntityIsActiveAsync(connection, "Patients", "IdPatient", request.IdPatient))
            return BadRequest(new { Message = "Patient does not exist or is inactive." });

        if (!await CheckIfEntityIsActiveAsync(connection, "Doctors", "IdDoctor", request.IdDoctor))
            return BadRequest(new { Message = "Doctor does not exist or is inactive." });

        if (await CheckDoctorConflictAsync(connection, request.IdDoctor, request.AppointmentDate))
            return Conflict(new { Message = "Doctor already has an appointment at this exact time." });

        await using var insertCommand = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, CreatedAt)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, 'Scheduled', @Reason, SYSUTCDATETIME());
            """, connection);

        insertCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        insertCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        insertCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        insertCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;

        var newId = (int)(await insertCommand.ExecuteScalarAsync() ?? 0);

        return CreatedAtAction(nameof(GetAppointmentDetails), new { id = newId }, new { IdAppointment = newId });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAppointment(int id, [FromBody] UpdateAppointmentRequestDto request)
    {
        var validStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!validStatuses.Contains(request.Status))
            return BadRequest(new { Message = "Invalid status." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var getCommand = new SqlCommand("SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @Id", connection);
        getCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        
        await using var reader = await getCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound(new { Message = $"Appointment with ID {id} not found." });

        var currentStatus = reader.GetString(0);
        var currentDate = reader.GetDateTime(1);
        await reader.CloseAsync();

        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
            return BadRequest(new { Message = "Cannot change the date of a completed appointment." });

        if (!await CheckIfEntityIsActiveAsync(connection, "Patients", "IdPatient", request.IdPatient))
            return BadRequest(new { Message = "Patient does not exist or is inactive." });
            
        if (!await CheckIfEntityIsActiveAsync(connection, "Doctors", "IdDoctor", request.IdDoctor))
            return BadRequest(new { Message = "Doctor does not exist or is inactive." });

        if (currentDate != request.AppointmentDate && await CheckDoctorConflictAsync(connection, request.IdDoctor, request.AppointmentDate))
            return Conflict(new { Message = "Doctor already has an appointment at this exact time." });

        await using var updateCommand = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @Id;
            """, connection);

        updateCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        updateCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        updateCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        updateCommand.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
        updateCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;
        updateCommand.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value = (object?)request.InternalNotes ?? DBNull.Value;
        updateCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        await updateCommand.ExecuteNonQueryAsync();

        return Ok(new { Message = "Appointment updated successfully." });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAppointment(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var checkCommand = new SqlCommand("SELECT Status FROM dbo.Appointments WHERE IdAppointment = @Id", connection);
        checkCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        var status = (string?)await checkCommand.ExecuteScalarAsync();

        if (status == null)
            return NotFound(new { Message = $"Appointment with ID {id} not found." });

        if (status == "Completed")
            return Conflict(new { Message = "Cannot delete a completed appointment." });

        await using var deleteCommand = new SqlCommand("DELETE FROM dbo.Appointments WHERE IdAppointment = @Id", connection);
        deleteCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        
        await deleteCommand.ExecuteNonQueryAsync();

        return NoContent();
    }

    private static async Task<bool> CheckIfEntityIsActiveAsync(SqlConnection connection, string tableName, string idColumnName, int id)
    {
        var query = $"SELECT IsActive FROM dbo.{tableName} WHERE {idColumnName} = @Id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        
        var result = await command.ExecuteScalarAsync();
        return result != null && (bool)result;
    }

    private static async Task<bool> CheckDoctorConflictAsync(SqlConnection connection, int doctorId, DateTime appointmentDate)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1) 
            FROM dbo.Appointments 
            WHERE IdDoctor = @IdDoctor 
              AND AppointmentDate = @AppointmentDate
              AND Status != 'Cancelled';
            """, connection);
            
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = doctorId;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;

        var count = (int)(await command.ExecuteScalarAsync() ?? 0);
        return count > 0;
    }
}