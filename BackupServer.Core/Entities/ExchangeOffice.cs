using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace BackupServer.Core.Entities
{
    public class ExchangeOffice
    {
        public int Id { get; set; }
        public int CityId { get; set; }
        public string Name { get; set; } = string.Empty; // Например: "SilkWay"

        public City City { get; set; } = null!;
        public ICollection<Point> Points { get; set; } = new List<Point>();
    }
}
