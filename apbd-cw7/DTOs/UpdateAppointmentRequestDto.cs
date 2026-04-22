namespace apbd_cw7.Controllers;
public record UpdateAppointmentRequestDto(
    int IdPatient,
    int IdDoctor,
    DateTime AppointmentDate,
    string Status,
    string Reason,
    string? InternalNotes
);