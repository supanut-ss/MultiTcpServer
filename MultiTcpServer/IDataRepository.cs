using System;

namespace MultiTcpServer
{
    /// <summary>
    /// Interface for data repository abstraction to support multiple storage backends
    /// (Database, File-based, etc.)
    /// </summary>
    public interface IDataRepository
    {
        /// <summary>
        /// Saves client data to the repository
        /// </summary>
        /// <param name="clientIp">IP address of the client</param>
        /// <param name="message">Data message from the client</param>
        void SaveData(string clientIp, string message);
    }
}
