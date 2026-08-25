using System;
using System.Collections.Generic;
using System.Text;

namespace BackupServer.Core.Entities
{
    public class City
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty; // Например: "Astana", "Almaty"

        public ICollection<ExchangeOffice> ExchangeOffices { get; set; } = new List<ExchangeOffice>();
    }
}
