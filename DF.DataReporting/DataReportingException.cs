namespace DF.DataReporting;

public class DataReportingException : Exception
{
    public DataReportingException()
    {
    }

    public DataReportingException(string message) : base(message)
    {
    }

    public DataReportingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}