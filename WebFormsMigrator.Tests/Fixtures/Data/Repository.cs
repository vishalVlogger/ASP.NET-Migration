using System.Data.SqlClient;
public class Repository { public void Load() { using var connection = new SqlConnection(); using var command = new SqlCommand(); command.CommandType = System.Data.CommandType.StoredProcedure; command.ExecuteReader(); } }
