namespace apbd_cw7.Controllers;

public record CreateAppointmentRequestDto(
    int IdPatient,
    int IdDoctor,
    DateTime AppointmentDate,
    string Reason
);