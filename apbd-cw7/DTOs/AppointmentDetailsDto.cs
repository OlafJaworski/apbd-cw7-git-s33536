namespace apbd_cw7.Controllers;

public record AppointmentDetailsDto(
    int IdAppointment,
    DateTime AppointmentDate,
    string Status,
    string Reason,
    string? InternalNotes,
    DateTime CreatedAt,
    string PatientFullName,
    string PatientEmail,
    string PatientPhoneNumber,
    string DoctorFullName,
    string DoctorSpecialization,
    string DoctorLicenseNumber
);