namespace apbd_cw7.Controllers;

public record AppointmentListDto(
    int IdAppointment,
    DateTime AppointmentDate,
    string Status,
    string Reason,
    string PatientFullName,
    string PatientEmail
);