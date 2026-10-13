namespace TutorArchier.Core.Hardware;

public interface IHardwareReportStore
{
    HardwareReport? Load();

    void Save(HardwareReport report);
}
