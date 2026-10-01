using System;

namespace MultiTcpServer
{
    /// <summary>
    /// Database-based implementation of IDataRepository
    /// Stores client data directly to the database using Entity Framework
    /// </summary>
    public class DatabaseRepository : IDataRepository
    {
        /// <summary>
        /// Saves client data to the database
        /// </summary>
        /// <param name="clientIp">IP address of the client</param>
        /// <param name="message">Data message from the client</param>
        public void SaveData(string clientIp, string message)
        {
            try
            {
                using (LIMS_BREntities model = new LIMS_BREntities())
                {
                    var entity = new t_interface_lims_machine_client
                    {
                        client_ip = clientIp,
                        data_time = DateTime.Now,
                        data_message = message
                    };
                    model.t_interface_lims_machine_client.Add(entity);
                    model.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Database error: {ex.Message}", ex);
            }
        }
    }
}
