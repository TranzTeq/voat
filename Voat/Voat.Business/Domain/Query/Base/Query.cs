#region LICENSE

/*
    
    Copyright(c) Voat, Inc.

    This file is part of Voat.

    This source file is subject to version 3 of the GPL license,
    that is bundled with this package in the file LICENSE, and is
    available online at http://www.gnu.org/licenses/gpl-3.0.txt;
    you may not use this file except in compliance with the License.

    Software distributed under the License is distributed on an
    "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either express
    or implied. See the License for the specific language governing
    rights and limitations under the License.

    All Rights Reserved.

*/

#endregion LICENSE

using System.Threading.Tasks;

namespace Voat.Domain.Query
{
    public interface IQuery<T>
    {
        Task<T> ExecuteAsync();
    }

    public abstract class Query<T> : IQuery<T>
    {
        private string _userName;

        public Query()
        {
            _userName = UserIdentity.UserName;
        }

        /// <summary>
        /// Represents the currently authenticated user name or the User who is executing/owns the context
        /// </summary>
        public string UserName
        {
            get
            {
                return _userName;
            }
        }

        //BLOCK: This needs fixed
        public abstract Task<T> ExecuteAsync();

        public virtual T Execute()
        {
            //BLOCK: This needs fixed
            Task<T> t = Task.Run(ExecuteAsync);
            Task.WaitAny(t);
            return t.Result;
        }
    }
}
